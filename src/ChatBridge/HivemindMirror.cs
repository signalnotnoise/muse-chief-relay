using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatBridge;

/// <summary>
/// Hands an accepted room chat to the Node HIVEMIND mirror. Off unless
/// <c>HIVEMIND_MESSAGE_MIRROR</c> is <c>1</c>. The chat path only enqueues;
/// Appwrite work runs on a background thread in the Node helper. A failure
/// here is a log line. It does not fail the bridge.
/// </summary>
internal interface IHivemindMirror
{
    void Offer(JsonObject chat, string channelFallback);
}

internal static class HivemindMirror
{
    public static IHivemindMirror? Open(BridgeRuntime runtime, Action<string> log, string? queueDirectory = null)
    {
        var env = runtime.Env ?? Environment.GetEnvironmentVariable;
        var flag = env("HIVEMIND_MESSAGE_MIRROR");
        if (!string.Equals(flag?.Trim(), "1", StringComparison.Ordinal))
            return null;
        if (runtime.MirrorOffer is { } offer)
            return new DelegateHivemindMirror(offer, log);
        var spill = string.IsNullOrWhiteSpace(queueDirectory)
            ? null
            : Path.Combine(queueDirectory, NodeHivemindMirror.SpillFileName);
        return NodeHivemindMirror.Start(env, log, spill);
    }
}

internal static class MirrorPayload
{
    private static readonly Regex Uuid = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Seconds ceiling for a stored HIVEMIND timestamp (year 5138).
    /// A voizle chat <c>ts</c> is Unix milliseconds from <c>Date.now()</c>
    /// and is larger than this. Those values are not stored raw.
    /// </summary>
    public const long TsMax = 100_000_000_000L;

    /// <summary>Inclusive ceiling for a Unix-millisecond stamp, equal to <see cref="TsMax"/> seconds.</summary>
    public const long TsMaxMillis = TsMax * 1000L;

    public readonly record struct Result(bool Ok, string Json, string Reason);

    /// <summary>Lowercase hex SHA-256 of the trimmed channel. Same bytes as the Node board key.</summary>
    public static string WorkspaceKey(string? channel)
    {
        var trimmed = (channel ?? "").Trim();
        if (trimmed.Length == 0)
            return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant();
    }

    /// <summary>
    /// JSON for the Node helper. The channel is hashed into <c>workspaceKey</c> and dropped.
    /// Trip, password, pass, and token are not copied.
    /// </summary>
    public static Result Build(JsonObject chat, string? channelFallback, long nowSeconds)
    {
        var id = Json.Str(chat, "id");
        if (id is null || !Uuid.IsMatch(id))
            return new Result(false, "", "id");
        var key = WorkspaceKey(Json.Str(chat, "channel") ?? Json.Str(chat, "room") ?? channelFallback);
        if (key.Length != 64)
            return new Result(false, "", "key");
        var sender = (Json.Str(chat, "nick") ?? "").Trim();
        if (sender.Length is 0 or > 64)
            return new Result(false, "", "sender");
        if (Json.Str(chat, "text") is not { } text || text.Length == 0)
            return new Result(false, "", "missing");
        if (text.Length > 8192)
            return new Result(false, "", "length");
        long ts;
        if (Epoch(chat, "time") is { } fromTime)
        {
            if (!ToEpochSeconds(fromTime, out ts))
                return new Result(false, "", "type");
        }
        else if (Epoch(chat, "ts") is { } fromTs)
        {
            if (!ToEpochSeconds(fromTs, out ts))
                return new Result(false, "", "type");
        }
        else
        {
            ts = nowSeconds;
        }

        if (ts < 0 || ts > TsMax)
            return new Result(false, "", "type");

        var payload = new JsonObject
        {
            ["id"] = id.ToLowerInvariant(),
            ["workspaceKey"] = key,
            ["sender"] = sender,
            ["text"] = text,
            ["ts"] = ts,
            ["threadKey"] = "room"
        };
        return new Result(true, payload.ToJsonString(JsonUtil.Opts), "");
    }

    private static long? Epoch(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var seconds))
            return seconds;
        if (value.TryGetValue<int>(out var small))
            return small;
        return null;
    }

    /// <summary>
    /// Seconds pass through. A value above <see cref="TsMax"/> and at most
    /// <see cref="TsMaxMillis"/> is Unix milliseconds and becomes whole seconds.
    /// </summary>
    private static bool ToEpochSeconds(long raw, out long seconds)
    {
        if (raw >= 0 && raw <= TsMax)
        {
            seconds = raw;
            return true;
        }

        if (raw > TsMax && raw <= TsMaxMillis)
        {
            seconds = raw / 1000L;
            return seconds >= 0 && seconds <= TsMax;
        }

        seconds = 0;
        return false;
    }
}

internal sealed class DelegateHivemindMirror(Action<string> offer, Action<string> log) : IHivemindMirror
{
    public void Offer(JsonObject chat, string channelFallback)
    {
        var built = MirrorPayload.Build(chat, channelFallback, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (!built.Ok)
        {
            log("hivemind message mirror refused (" + built.Reason + ")");
            return;
        }

        offer(built.Json);
    }
}

internal sealed class NodeHivemindMirror : IHivemindMirror
{
    /// <summary>
    /// In-memory offers waiting on the helper. A full queue is not dropped:
    /// the payload is appended to <see cref="SpillFileName"/> and drained
    /// when a slot is free. Only a spill that cannot be written is refused.
    /// </summary>
    internal const int MaxQueued = 32;

    /// <summary>Durable overflow beside inbox.jsonl. One helper JSON object per line.</summary>
    internal const string SpillFileName = "hivemind-mirror-queue.jsonl";

    private readonly Func<string, string?> _env;
    private readonly Action<string> _log;
    private readonly string? _script;
    private readonly string? _spillPath;
    private readonly Action<string>? _runOne;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private int _scriptMissing;
    private int _threadStarted;
    private long _spillOffset;
    private long _spillReadTo;
    private bool _tookSpill;

    private NodeHivemindMirror(
        Func<string, string?> env,
        Action<string> log,
        string? script,
        string? spillPath,
        Action<string>? runOne,
        bool start)
    {
        _env = env;
        _log = log;
        _script = script;
        _spillPath = spillPath;
        _runOne = runOne;
        if (start)
            Start();
    }

    public static NodeHivemindMirror Start(Func<string, string?> env, Action<string> log, string? spillPath = null) =>
        new(env, log, FindScript(env), spillPath, null, start: true);

    /// <summary>
    /// Test stand-in. <paramref name="runOne"/> receives the helper JSON.
    /// Pass <paramref name="start"/> false to fill the queue before the worker runs.
    /// </summary>
    internal static NodeHivemindMirror StartForTest(Action<string> log, string? spillPath, Action<string> runOne, bool start = true) =>
        new(_ => null, log, null, spillPath, runOne, start);

    /// <summary>Starts the worker once. A second call does nothing.</summary>
    internal void Start()
    {
        if (Interlocked.Exchange(ref _threadStarted, 1) != 0)
            return;
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "hivemind-mirror"
        };
        thread.Start();
        if (SpillPending())
            _signal.Release();
    }

    public void Offer(JsonObject chat, string channelFallback)
    {
        var built = MirrorPayload.Build(chat, channelFallback, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (!built.Ok)
        {
            _log("hivemind message mirror refused (" + built.Reason + ")");
            return;
        }

        var kept = false;
        var deferred = false;
        lock (_gate)
        {
            if (_pending.Count < MaxQueued)
            {
                _pending.Enqueue(built.Json);
                kept = true;
            }
            else if (TryAppendSpill(built.Json))
            {
                kept = true;
                deferred = true;
            }
        }

        if (kept)
            _signal.Release();
        if (deferred)
            _log("hivemind message mirror deferred (queue)");
        else if (!kept)
            _log("hivemind message mirror refused (queue)");
    }

    private void Loop()
    {
        while (true)
        {
            try
            {
                _signal.Wait();
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                while (TryTake(out var json))
                {
                    try
                    {
                        if (_runOne is not null)
                            _runOne(json);
                        else
                            RunOne(json);
                    }
                    catch (Exception)
                    {
                        _log("hivemind message mirror failed (error)");
                    }

                    CommitSpillTake();
                }
            }
            catch (Exception)
            {
                _log("hivemind message mirror failed (error)");
            }
        }
    }

    private bool TryTake(out string json)
    {
        lock (_gate)
        {
            _tookSpill = false;
            if (_pending.TryDequeue(out var queued) && queued is not null)
            {
                json = queued;
                return true;
            }

            while (TryReadSpillLine(out var line, out var next))
            {
                if (line.Length == 0 || !SpillLineOk(line))
                {
                    if (line.Length > 0)
                        _log("hivemind message mirror refused (json)");
                    _spillOffset = next;
                    TryDeleteSpillIfDrained();
                    continue;
                }

                json = line;
                _tookSpill = true;
                _spillReadTo = next;
                return true;
            }

            json = "";
            return false;
        }
    }

    private void CommitSpillTake()
    {
        if (!_tookSpill)
            return;
        lock (_gate)
        {
            if (!_tookSpill)
                return;
            _spillOffset = _spillReadTo;
            _tookSpill = false;
            TryDeleteSpillIfDrained();
        }
    }

    private bool SpillPending()
    {
        if (string.IsNullOrEmpty(_spillPath))
            return false;
        try
        {
            return File.Exists(_spillPath) && new FileInfo(_spillPath).Length > _spillOffset;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Append one helper JSON line. A torn tail gets its own newline first so
    /// the next record stays a separate line. False when there is no path or
    /// the write fails. The caller logs that as a queue refuse.
    /// </summary>
    private bool TryAppendSpill(string json)
    {
        if (string.IsNullOrEmpty(_spillPath))
            return false;
        try
        {
            var dir = Path.GetDirectoryName(_spillPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Same durability as inbox.jsonl (append, no fsync). Offer runs on the
            // receive path; a disk flush here would stall the socket during a burst.
            using var fs = new FileStream(
                _spillPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);
            if (fs.Length > 0)
            {
                fs.Position = fs.Length - 1;
                var last = fs.ReadByte();
                if (last != '\n')
                    fs.WriteByte((byte)'\n');
            }

            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            fs.Write(bytes);
            fs.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryReadSpillLine(out string line, out long nextOffset)
    {
        line = "";
        nextOffset = _spillOffset;
        if (string.IsNullOrEmpty(_spillPath))
            return false;

        byte[] chunk;
        try
        {
            using var fs = new FileStream(_spillPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _spillOffset)
                _spillOffset = 0;
            var available = fs.Length - _spillOffset;
            if (available <= 0)
                return false;
            var take = (int)Math.Min(available, 256 * 1024);
            chunk = new byte[take];
            fs.Position = _spillOffset;
            fs.ReadExactly(chunk);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            _log("hivemind message mirror failed (spill)");
            return false;
        }

        var nl = Array.IndexOf(chunk, (byte)'\n');
        if (nl < 0)
        {
            // A mirror payload is one short JSON line. A chunk this large with
            // no newline is corrupt. Hand back a non-empty stand-in so the
            // caller logs refused (json) and skips it. The stand-in is not
            // file bytes, and the log line does not include them.
            if (chunk.Length < 256 * 1024)
                return false;
            nextOffset = _spillOffset + chunk.Length;
            line = " ";
            return true;
        }

        nextOffset = _spillOffset + nl + 1;
        line = Encoding.UTF8.GetString(chunk, 0, nl).TrimEnd('\r');
        return true;
    }

    private void TryDeleteSpillIfDrained()
    {
        if (string.IsNullOrEmpty(_spillPath) || !File.Exists(_spillPath))
        {
            _spillOffset = 0;
            return;
        }

        long length;
        try
        {
            length = new FileInfo(_spillPath).Length;
        }
        catch (Exception)
        {
            return;
        }

        if (length != _spillOffset)
            return;
        try
        {
            File.Delete(_spillPath);
            _spillOffset = 0;
        }
        catch (Exception)
        {
            _log("hivemind message mirror failed (spill)");
        }
    }

    private static bool SpillLineOk(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private void RunOne(string json)
    {
        if (string.IsNullOrEmpty(_script) || !File.Exists(_script))
        {
            if (Interlocked.Exchange(ref _scriptMissing, 1) == 0)
                _log("hivemind message mirror failed (script)");
            return;
        }

        var node = _env("HIVEMIND_MIRROR_NODE");
        if (string.IsNullOrWhiteSpace(node))
            node = "node";
        var psi = new ProcessStartInfo
        {
            FileName = node.Trim(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(_script);
        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
            {
                _log("hivemind message mirror failed (spawn)");
                return;
            }
        }
        catch (Exception)
        {
            _log("hivemind message mirror failed (spawn)");
            return;
        }

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        try
        {
            proc.StandardInput.Write(json);
            proc.StandardInput.Close();
        }
        catch (Exception)
        {
            TryKill(proc);
            _log("hivemind message mirror failed (stdin)");
            return;
        }

        if (!proc.WaitForExit(35_000))
        {
            TryKill(proc);
            try { proc.WaitForExit(2_000); }
            catch (Exception) { /* already gone */ }
            _log("hivemind message mirror failed (timeout)");
            return;
        }

        // Drain so the process can leave. Forward only the helper's own status
        // lines. A nonzero exit that printed none of those still gets one log.
        _ = stdout.GetAwaiter().GetResult();
        LogHelperResult(stderr.GetAwaiter().GetResult(), proc.ExitCode, _log);
    }

    internal static void LogHelperResult(string stderr, int exitCode, Action<string> log)
    {
        var forwarded = false;
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!SafeLine(trimmed))
                continue;
            log(trimmed);
            forwarded = true;
        }

        if (exitCode != 0 && !forwarded)
            log("hivemind message mirror failed (exit)");
    }

    private static bool SafeLine(string line) =>
        Regex.IsMatch(
            line,
            "^hivemind [a-z0-9 -]+ (failed|refused) \\([A-Za-z0-9_-]+\\)$",
            RegexOptions.CultureInvariant);

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // already gone
        }
    }

    private static string? FindScript(Func<string, string?> env)
    {
        var over = env("HIVEMIND_MIRROR_SCRIPT");
        if (!string.IsNullOrWhiteSpace(over))
            return over.Trim();
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "web", "muse", "messageMirror.js");
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }

        return null;
    }
}
