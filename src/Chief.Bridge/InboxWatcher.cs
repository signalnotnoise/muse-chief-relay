using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chief.Bridge;

internal delegate void LineHandler(ReadOnlySpan<byte> line);

/// <summary>One new inbound chat, as printed by <c>watch</c>.</summary>
internal sealed record WatchedChat(string? Nick, string? Trip, string? Text, JsonNode? Ts)
{
    public JsonObject ToJson() => new()
    {
        ["nick"] = Nick,
        ["trip"] = Trip,
        ["text"] = Text,
        ["ts"] = Ts?.DeepClone()
    };

    public static string ToJsonArray(IEnumerable<WatchedChat> chats) =>
        new JsonArray(chats.Select(c => (JsonNode)c.ToJson()).ToArray()).ToJsonString(JsonUtil.Opts);
}

/// <summary>Saved read position: a byte offset that always sits on a line boundary, plus a hash of the
/// file's first <c>min(256, offset)</c> bytes so a rotated inbox is noticed even if the new file has already
/// grown past the offset. Below 256 bytes the hash covers every consumed byte; it never covers bytes past the
/// offset, which may still be growing.</summary>
internal sealed record WatchOffset(long Offset, string? Head);

/// <summary>Result of one poll. Nothing is saved until <see cref="InboxWatcher.Commit"/>.</summary>
internal sealed record WatchPoll(
    IReadOnlyList<WatchedChat> Chats,
    WatchOffset Next,
    bool Bootstrapped,
    bool Reset,
    bool Dirty);

/// <summary>Reported to <see cref="InboxWatcher.WaitAsync"/>'s <c>onTick</c> on every loop.</summary>
internal enum WatchPhase
{
    /// <summary>Nothing new yet.</summary>
    Waiting,

    /// <summary>At least one chat arrived; waiting out <c>--settle</c> so a burst comes back as one wake.</summary>
    Settling
}

internal enum WaitOutcome
{
    Delivered,
    TimedOut,
    Cancelled
}

/// <summary>
/// Reads new inbound <c>chat</c> frames from a Chief.Bridge <c>inbox.jsonl</c>, remembering where it
/// stopped in an offset file.
/// <list type="bullet">
/// <item>The offset is in bytes, and only complete, newline-terminated lines are consumed: a line the
/// bridge is still writing is left for the next poll.</item>
/// <item>First run (no offset file): record the end of the last complete line and report nothing, so
/// history never floods the first poll. If the inbox doesn't exist yet, record 0.</item>
/// <item>Truncated inbox (shorter than the offset) or rotated inbox (its first bytes changed): start again
/// from 0 and report what the new file holds.</item>
/// <item>Frames that aren't inbound chats, the bridge's own nick, blank and malformed lines are skipped
/// (and consumed).</item>
/// <item>The offset file is written atomically (temp file, then rename). One watcher per offset file.</item>
/// <item>A poll that hits transient filesystem trouble (a torn read, a locked file) doesn't stop a
/// <c>--wait</c>: the failure is recorded as a warning and the next loop retries, so the listener
/// stays up instead of dying quietly.</item>
/// </list>
/// </summary>
internal class InboxWatcher
{
    public const int HeadBytes = 256;
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly string _inbox;
    private readonly string _offsetPath;
    private readonly string _ownNick;
    private readonly TimeSpan _pollInterval;

    public InboxWatcher(string inboxPath, string offsetPath, string ownNick, TimeSpan? pollInterval = null)
    {
        _inbox = inboxPath;
        _offsetPath = offsetPath;
        _ownNick = ownNick;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>Set when the offset file existed but couldn't be read; the watcher re-bootstraps. It stays set
    /// for the life of this instance (later polls read the rewritten file fine), so a caller that checks it after
    /// <see cref="WaitAsync"/> still sees it.</summary>
    public string? Warning { get; private set; }

    public virtual WatchPoll Poll()
    {
        var saved = ReadOffset(out var warning);
        Warning ??= warning;

        if (!File.Exists(_inbox))
        {
            // Nothing to read. On a first run, record 0 so everything in the inbox once it appears is new.
            return saved is null
                ? new WatchPoll(Array.Empty<WatchedChat>(), new WatchOffset(0, HashHead(Array.Empty<byte>())), true, false, true)
                : new WatchPoll(Array.Empty<WatchedChat>(), saved, false, false, false);
        }

        using var fs = new FileStream(_inbox, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;

        if (saved is null)
        {
            var end = LastLineEnd(fs, length);
            return new WatchPoll(Array.Empty<WatchedChat>(), new WatchOffset(end, Head(fs, end)), true, false, true);
        }

        var start = saved.Offset;
        var reset = false;
        if (length < start)
        {
            start = 0; // truncated
            reset = true;
        }
        else if (saved.Head is not null && Head(fs, start) != saved.Head)
        {
            start = 0; // replaced by a different file
            reset = true;
        }

        var chats = new List<WatchedChat>();
        var next = ReadCompleteLines(fs, start, length, line =>
        {
            if (ParseLine(line, _ownNick) is { } chat)
                chats.Add(chat);
        });

        var nextOffset = new WatchOffset(next, Head(fs, next));
        var dirty = reset || nextOffset != saved;
        return new WatchPoll(chats, nextOffset, false, reset, dirty);
    }

    /// <summary>
    /// One poll that never throws on filesystem trouble: returns false with a message instead, so a
    /// long-running <c>watch --wait</c> survives a transient read failure and retries on the next loop
    /// rather than dying unnoticed. Tests override <see cref="Poll"/> to inject failures.
    /// </summary>
    internal bool TryPoll(out WatchPoll? poll, out string? error)
    {
        try
        {
            poll = Poll();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            poll = null;
            error = ex.Message;
            return false;
        }
    }

    public void Commit(WatchPoll poll)
    {
        if (poll.Dirty)
            WriteOffset(poll.Next);
    }

    /// <summary>
    /// Block until at least one new qualifying chat arrives, hand it to <paramref name="deliver"/>, then save
    /// the offset (deliver first, save second: a crash in between repeats a message rather than losing it).
    /// Lines that don't qualify are consumed as they go by. Wakes on file-system events, with a poll every
    /// <c>pollInterval</c> as the fallback.
    /// <para>
    /// With <paramref name="settle"/> &gt; 0, the first chat doesn't return at once: the watcher keeps
    /// collecting until <paramref name="settle"/> passes with no further qualifying chat, or
    /// <see cref="SettleCapFactor"/> × <paramref name="settle"/> after the first one, whichever comes first, and
    /// returns them all in one delivery. Once a chat is in hand it is always delivered: neither the timeout nor
    /// cancellation during the settle window drops it.
    /// </para>
    /// <paramref name="onTick"/> is called on every loop (at least every <c>pollInterval</c>), for heartbeats.
    /// </summary>
    public async Task<WaitOutcome> WaitAsync(
        TimeSpan? timeout, Action<IReadOnlyList<WatchedChat>> deliver, CancellationToken ct,
        TimeSpan? settle = null, Action<WatchPhase>? onTick = null)
    {
        var now = DateTime.UtcNow;
        // A timeout that reaches past DateTime.MaxValue is as good as none (and would overflow the addition).
        var deadline = timeout is { } t && t < DateTime.MaxValue - now ? now + t : (DateTime?)null;
        using var changed = new SemaphoreSlim(0, 1);
        using var fsw = TryWatchDirectory(changed);

        while (true)
        {
            // A failed poll is transient trouble (a torn read, a locked file), not a reason to stop
            // listening: warn once and retry on the next loop. A listener that dies quietly is how
            // messages pile up unnoticed.
            var poll = TryPoll(out var p, out var error) ? p : null;
            if (poll is null)
            {
                Warning ??= $"inbox poll failed ({error}); retrying";
            }
            else if (poll.Chats.Count > 0)
            {
                if (settle is { } st && st > TimeSpan.Zero)
                    poll = await SettleAsync(poll, st, changed, onTick, ct);
                deliver(poll.Chats);
                Commit(poll);
                return WaitOutcome.Delivered;
            }
            else
            {
                Commit(poll);
            }

            if (ct.IsCancellationRequested)
                return WaitOutcome.Cancelled;
            onTick?.Invoke(WatchPhase.Waiting);

            var wait = _pollInterval;
            if (deadline is { } d)
            {
                var remaining = d - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return WaitOutcome.TimedOut;
                if (remaining < wait)
                    wait = remaining;
            }

            try
            {
                await changed.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return WaitOutcome.Cancelled;
            }
        }
    }

    /// <summary>The settle window never runs longer than this many times <c>--settle</c> after the first chat.</summary>
    public const int SettleCapFactor = 4;

    /// <summary>Re-polls (nothing is committed in between, so each poll covers everything since the saved offset)
    /// until the burst goes quiet for <paramref name="settle"/> or the cap is reached. Returns the latest poll.</summary>
    private async Task<WatchPoll> SettleAsync(
        WatchPoll first, TimeSpan settle, SemaphoreSlim changed, Action<WatchPhase>? onTick, CancellationToken ct)
    {
        var latest = first;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var cap = TimeSpan.FromTicks(settle.Ticks * SettleCapFactor);
        var lastNew = TimeSpan.Zero;

        while (true)
        {
            onTick?.Invoke(WatchPhase.Settling);
            var elapsed = clock.Elapsed;
            var wait = new[] { _pollInterval, lastNew + settle - elapsed, cap - elapsed }.Min();
            if (wait <= TimeSpan.Zero)
                return latest;

            try
            {
                await changed.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return latest; // stopping: deliver what we already have
            }

            var p = TryPoll(out var fresh, out var error) ? fresh : null;
            if (p is null)
            {
                // Transient trouble mid-burst: keep what we have and keep waiting. The quiet-timer and
                // the cap still bound the window, and the chats in hand are always delivered.
                Warning ??= $"inbox poll failed during settle ({error}); retrying";
            }
            else
            {
                if (p.Chats.Count > latest.Chats.Count)
                    lastNew = clock.Elapsed;
                latest = p;
            }
        }
    }

    internal static WatchedChat? ParseLine(ReadOnlySpan<byte> line, string ownNick)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null; // malformed or blank
        }

        if (node is not JsonObject row || Json.Str(row, "dir") != "in")
            return null;
        if (row["msg"] is not JsonObject msg || Json.Str(msg, "cmd") != "chat")
            return null;

        var nick = Json.Str(msg, "nick");
        if (string.Equals(nick, ownNick, StringComparison.Ordinal))
            return null;

        return new WatchedChat(nick, Json.Str(msg, "trip"), Json.Str(msg, "text"), row["ts"]?.DeepClone());
    }

    /// <summary>Calls <paramref name="onLine"/> for each complete line in [start, length) and returns the
    /// offset just past the last newline (or <paramref name="start"/> if there was none).</summary>
    private static long ReadCompleteLines(FileStream fs, long start, long length, LineHandler onLine)
    {
        var buffer = new byte[64 * 1024];
        var current = new MemoryStream();
        var pos = start;
        var consumed = start;
        fs.Seek(start, SeekOrigin.Begin);

        while (pos < length)
        {
            var toRead = (int)Math.Min(buffer.Length, length - pos);
            var n = fs.Read(buffer, 0, toRead);
            if (n <= 0)
                break;

            var segStart = 0;
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                current.Write(buffer, segStart, i - segStart);
                onLine(current.GetBuffer().AsSpan(0, (int)current.Length));
                current.SetLength(0);
                segStart = i + 1;
                consumed = pos + i + 1;
            }

            current.Write(buffer, segStart, n - segStart);
            pos += n;
        }

        return consumed;
    }

    private static long LastLineEnd(FileStream fs, long length)
    {
        var buffer = new byte[4096];
        var end = length;
        while (end > 0)
        {
            var size = (int)Math.Min(buffer.Length, end);
            fs.Seek(end - size, SeekOrigin.Begin);
            fs.ReadExactly(buffer, 0, size);
            var idx = Array.LastIndexOf(buffer, (byte)'\n', size - 1);
            if (idx >= 0)
                return end - size + idx + 1;
            end -= size;
        }

        return 0;
    }

    private static string Head(FileStream fs, long upTo)
    {
        var n = (int)Math.Min(HeadBytes, upTo);
        var bytes = new byte[n];
        fs.Seek(0, SeekOrigin.Begin);
        fs.ReadExactly(bytes, 0, n);
        return HashHead(bytes);
    }

    private static string HashHead(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private WatchOffset? ReadOffset(out string? warning)
    {
        warning = null;
        if (!File.Exists(_offsetPath))
            return null;

        var text = File.ReadAllText(_offsetPath).Trim();
        if (long.TryParse(text, out var bare) && bare >= 0)
            return new WatchOffset(bare, null); // a bare number (hand-written): no rotation check

        try
        {
            // The writer always emits both fields. A JSON offset without a valid head is corrupt: accepting it
            // would silently switch off rotation detection, so it takes the warning/re-bootstrap path below.
            if (JsonNode.Parse(text) is JsonObject o
                && o["offset"] is JsonValue ov && ov.TryGetValue<long>(out var off) && off >= 0
                && Json.Str(o, "head") is { } head && IsHeadHash(head))
            {
                return new WatchOffset(off, head);
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        warning = $"offset file {_offsetPath} is unreadable or corrupt; starting over from the end of the inbox";
        return null;
    }

    /// <summary>What <see cref="HashHead"/> produces: 64 lowercase hex digits.</summary>
    private static bool IsHeadHash(string s) =>
        s.Length == 64 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void WriteOffset(WatchOffset offset)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_offsetPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = new JsonObject { ["offset"] = offset.Offset, ["head"] = offset.Head }.ToJsonString(JsonUtil.Opts);
        var tmp = $"{_offsetPath}.tmp.{Environment.ProcessId}";
        File.WriteAllText(tmp, json + "\n");
        File.Move(tmp, _offsetPath, overwrite: true);
    }

    private FileSystemWatcher? TryWatchDirectory(SemaphoreSlim changed)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_inbox));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var fsw = new FileSystemWatcher(dir, Path.GetFileName(_inbox))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            void Signal(object? _, EventArgs __)
            {
                try
                {
                    if (changed.CurrentCount == 0)
                        changed.Release();
                }
                catch (Exception)
                {
                    // already full or disposed: the next poll picks it up
                }
            }

            fsw.Changed += Signal;
            fsw.Created += Signal;
            fsw.Renamed += Signal;
            fsw.Deleted += Signal;
            fsw.Error += (_, _) => { /* the 1 s poll keeps working */ };
            fsw.EnableRaisingEvents = true;
            return fsw;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // e.g. inotify limits: fall back to polling only
        }
    }
}

/// <summary>Options for <c>watch</c>, after the global <c>--config</c> has been taken out.</summary>
internal sealed record WatchOptions(string? Nick, string? StatePath, bool Wait, TimeSpan? Timeout, TimeSpan? Settle = null)
{
    /// <summary>Largest accepted <c>--settle</c>, in seconds. A longer window only delays every wake.</summary>
    public const double MaxSettleSeconds = 60;

    public const int ExitTimeout = 3;

    /// <summary>Largest accepted <c>--timeout</c>: whole seconds of <see cref="TimeSpan.MaxValue"/> (about 29,000 years).</summary>
    public static readonly double MaxTimeoutSeconds = Math.Floor(TimeSpan.MaxValue.TotalSeconds);

    public static WatchOptions Parse(IReadOnlyList<string> args)
    {
        string? nick = null, state = null;
        var wait = false;
        TimeSpan? timeout = null, settle = null;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Value(string name)
            {
                if (a.StartsWith(name + "=", StringComparison.Ordinal))
                    return a[(name.Length + 1)..];
                if (i + 1 >= args.Count)
                    throw new ArgumentException($"{name} needs a value");
                return args[++i];
            }

            if (a == "--nick" || a.StartsWith("--nick=", StringComparison.Ordinal))
                nick = Value("--nick");
            else if (a == "--state" || a.StartsWith("--state=", StringComparison.Ordinal))
                state = Value("--state");
            else if (a == "--wait")
                wait = true;
            else if (a == "--timeout" || a.StartsWith("--timeout=", StringComparison.Ordinal))
            {
                var v = Value("--timeout");
                // Reject NaN/infinity and anything TimeSpan can't hold (e.g. 1e308), so every bad value is a
                // usage error (exit 2) rather than an OverflowException.
                if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var secs)
                    || !double.IsFinite(secs) || secs < 0 || secs > MaxTimeoutSeconds)
                    throw new ArgumentException(
                        $"--timeout needs a number of seconds from 0 to {MaxTimeoutSeconds:0}, got '{v}'");
                timeout = TimeSpan.FromSeconds(secs);
            }
            else if (a == "--settle" || a.StartsWith("--settle=", StringComparison.Ordinal))
            {
                var v = Value("--settle");
                if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var secs)
                    || !double.IsFinite(secs) || secs < 0 || secs > MaxSettleSeconds)
                    throw new ArgumentException(
                        $"--settle needs a number of seconds from 0 to {MaxSettleSeconds:0}, got '{v}'");
                settle = TimeSpan.FromSeconds(secs);
            }
            else
                throw new ArgumentException($"watch: unknown argument '{a}'");
        }

        if (nick is not null && string.IsNullOrWhiteSpace(nick))
            throw new ArgumentException("--nick needs a value");
        if (state is not null && string.IsNullOrWhiteSpace(state))
            throw new ArgumentException("--state needs a value");
        if (timeout is not null && !wait)
            throw new ArgumentException("--timeout only applies with --wait");
        if (settle is not null && !wait)
            throw new ArgumentException("--settle only applies with --wait");

        return new WatchOptions(nick, state, wait, timeout, settle);
    }
}
