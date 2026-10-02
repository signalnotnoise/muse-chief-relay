using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatBridge;

/// <summary>Immutable delivery row in an agent's inbox.jsonl.</summary>
internal sealed class InboxEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
    [JsonPropertyName("room")] public string Room { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("trip")] public string? Trip { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("mentions")] public List<string> Mentions { get; set; } = new();
    [JsonPropertyName("scope")] public string Scope { get; set; } = InboxContract.RoomScope;
    [JsonPropertyName("hop")] public int Hop { get; set; }
    [JsonPropertyName("fanout")] public bool Fanout { get; set; }

    /// <summary>Source id of the causal parent. Null when this event starts a chain.</summary>
    [JsonPropertyName("parent")] public string? Parent { get; set; }

    /// <summary>Source id of the chain root. Fan-out hops are counted inside one root.</summary>
    [JsonPropertyName("root")] public string? Root { get; set; }

    [JsonPropertyName("ts")] public long? Ts { get; set; }
}

internal sealed record InboxView(InboxEvent Event, int Attempts, long? NextUnix, string? Error)
{
    public bool IsDue(DateTimeOffset now) => NextUnix is null || NextUnix <= now.ToUnixTimeSeconds();
}

internal enum InboxWriteKind
{
    Stored,
    Duplicate,
    /// <summary>On deferred.jsonl because the inbox lock timed out. Not in inbox.jsonl yet.</summary>
    Deferred
}

internal readonly record struct InboxWrite(InboxWriteKind Kind, InboxEvent Event);

internal static class InboxBackoff
{
    /// <summary>1s, 2s, 4s, 8s, 16s, then 30s. <paramref name="attempts"/> is 1 after the first failure.</summary>
    public static TimeSpan Delay(int attempts)
    {
        var shift = Math.Clamp(attempts - 1, 0, 5);
        return TimeSpan.FromSeconds(Math.Min(30, 1 << shift));
    }
}

/// <summary>
/// Durable per-agent queue. inbox.jsonl is append-only. Acks and retry state are append-only in
/// control.jsonl, so a restart reloads both and continues in seq order. A mention that cannot take
/// the inbox lock is appended to deferred.jsonl and filed on the next successful lock.
/// </summary>
internal sealed class AgentInbox
{
    public static readonly TimeSpan DefaultLockBudget = TimeSpan.FromSeconds(2);
    internal const string DeferredName = "deferred.jsonl";
    private const string LockBusyMessage = "inbox lock busy";

    private readonly string _dir;
    private readonly TimeSpan _lockBudget;
    private readonly List<InboxEvent> _events = new();
    private readonly Dictionary<string, InboxEvent> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sourceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    private AgentInbox(string dir, TimeSpan lockBudget)
    {
        _dir = dir;
        _lockBudget = lockBudget < TimeSpan.Zero ? TimeSpan.Zero : lockBudget;
    }

    public string DirectoryPath => _dir;
    public string InboxPath => Path.Combine(_dir, "inbox.jsonl");

    /// <summary>Invoked for each event newly appended to inbox.jsonl, including a drained deferral.</summary>
    internal Action<InboxEvent>? OnFiled { get; set; }

    public static AgentInbox Open(string agentDir, TimeSpan? lockBudget = null)
    {
        Directory.CreateDirectory(agentDir);
        var inbox = new AgentInbox(agentDir, lockBudget ?? DefaultLockBudget);
        inbox.Reload();
        return inbox;
    }

    /// <summary>File any mentions spilled while the inbox lock was busy.</summary>
    public void CatchUp() => Pending();

    public bool TryAppend(InboxEvent draft, out InboxEvent stored)
    {
        var write = Write(draft);
        stored = write.Event;
        return write.Kind == InboxWriteKind.Stored;
    }

    public InboxWrite Write(InboxEvent draft)
    {
        try
        {
            return UnderLock(filed => AppendOrDuplicate(draft, filed));
        }
        catch (IOException ex) when (IsLockBusy(ex))
        {
            Spill(draft);
            return new InboxWrite(InboxWriteKind.Deferred, draft);
        }
    }

    public IReadOnlyList<InboxView> Pending() =>
        UnderLock(_ => Ordered().ToList());

    /// <summary>Unacked events in seq order, stopping before the first one still in backoff.</summary>
    public IReadOnlyList<InboxView> Due(DateTimeOffset now)
    {
        var ready = new List<InboxView>();
        foreach (var view in Pending())
        {
            if (!view.IsDue(now))
                break;
            ready.Add(view);
        }

        return ready;
    }

    public bool Ack(string id, DateTimeOffset now) =>
        UnderLock(_ =>
        {
            if (!_byId.ContainsKey(id))
                return false;
            if (_acked.Contains(id))
                return true;
            AppendJson(ControlPath, new ControlLine { Op = "ack", Id = id, At = now.ToUnixTimeSeconds() });
            _acked.Add(id);
            return true;
        });

    public bool RecordFailure(string id, string error, DateTimeOffset now) =>
        UnderLock(_ =>
        {
            if (!_byId.ContainsKey(id) || _acked.Contains(id))
                return false;
            var prev = _attempts.GetValueOrDefault(id)?.Count ?? 0;
            var attempts = prev + 1;
            var next = now + InboxBackoff.Delay(attempts);
            var line = new ControlLine
            {
                Op = "attempt",
                Id = id,
                Attempts = attempts,
                NextUnix = next.ToUnixTimeSeconds(),
                Error = error
            };
            AppendJson(ControlPath, line);
            _attempts[id] = new AttemptState(attempts, line.NextUnix, error);
            return true;
        });

    /// <summary>
    /// Highest <c>hop</c> on a fan-out row whose chain root is <paramref name="root"/>, acked or not.
    /// Zero when <paramref name="root"/> is empty or this inbox has no fan-out in that chain.
    /// Other chains in the same file do not count.
    /// </summary>
    public int HighestFanoutHop(string? root)
    {
        if (string.IsNullOrEmpty(root))
            return 0;
        return UnderLock(_ =>
        {
            var max = 0;
            foreach (var ev in _events)
            {
                if (!ev.Fanout || ev.Hop <= max)
                    continue;
                if (string.Equals(ChainRoot(ev), root, StringComparison.Ordinal))
                    max = ev.Hop;
            }

            return max;
        });
    }

    /// <summary>Delivery with this id or source id, acked or not.</summary>
    public InboxEvent? Find(string? idOrSource)
    {
        if (string.IsNullOrEmpty(idOrSource))
            return null;
        return UnderLock(_ =>
        {
            if (_byId.TryGetValue(idOrSource, out var byId))
                return byId;
            return _events.FirstOrDefault(e =>
                string.Equals(e.SourceId, idOrSource, StringComparison.Ordinal)
                || string.Equals(e.Id, idOrSource, StringComparison.Ordinal));
        });
    }

    /// <summary>Highest-seq delivery in this inbox, acked or not. Null when the inbox has none.</summary>
    public InboxEvent? Latest() =>
        UnderLock(_ =>
        {
            InboxEvent? best = null;
            foreach (var ev in _events)
            {
                if (best is null || ev.Seq > best.Seq)
                    best = ev;
            }

            return best;
        });

    internal static string ChainRoot(InboxEvent ev) =>
        string.IsNullOrEmpty(ev.Root) ? ev.SourceId : ev.Root;

    private IEnumerable<InboxView> Ordered() =>
        _events
            .Where(e => !_acked.Contains(e.Id))
            .OrderBy(e => e.Seq)
            .Select(e =>
            {
                var attempt = _attempts.GetValueOrDefault(e.Id);
                return new InboxView(e, attempt?.Count ?? 0, attempt?.NextUnix, attempt?.Error);
            });

    private InboxEvent? FindSource(string sourceId) =>
        _events.FirstOrDefault(e => string.Equals(e.SourceId, sourceId, StringComparison.Ordinal));

    private InboxWrite AppendOrDuplicate(InboxEvent draft, List<InboxEvent> filed)
    {
        if (string.IsNullOrEmpty(draft.Id) || string.IsNullOrEmpty(draft.SourceId))
            throw new IOException("inbox event needs an id and a source id");
        if (_byId.TryGetValue(draft.Id, out var existing))
            return new InboxWrite(InboxWriteKind.Duplicate, existing);
        var bySource = FindSource(draft.SourceId);
        if (bySource is not null)
            return new InboxWrite(InboxWriteKind.Duplicate, bySource);

        var stored = Materialize(draft);
        AppendJson(InboxPath, stored);
        Remember(stored);
        filed.Add(stored);
        return new InboxWrite(InboxWriteKind.Stored, stored);
    }

    private InboxEvent Materialize(InboxEvent draft)
    {
        var seq = _events.Count == 0 ? 1 : _events.Max(e => e.Seq) + 1;
        return new InboxEvent
        {
            Id = draft.Id,
            Seq = seq,
            Agent = draft.Agent,
            SourceId = draft.SourceId,
            Room = draft.Room,
            From = draft.From,
            Trip = draft.Trip,
            Text = draft.Text,
            Mentions = draft.Mentions?.ToList() ?? new List<string>(),
            Scope = InboxContract.RoomScope,
            Hop = draft.Hop,
            Fanout = draft.Fanout,
            Parent = string.IsNullOrEmpty(draft.Parent) ? null : draft.Parent,
            Root = string.IsNullOrEmpty(draft.Root) ? null : draft.Root,
            Ts = draft.Ts
        };
    }

    private void Remember(InboxEvent stored)
    {
        _events.Add(stored);
        _byId[stored.Id] = stored;
        _sourceIds.Add(stored.SourceId);
    }

    private void RepairAndReload(List<InboxEvent> filed)
    {
        RepairTornTail(InboxPath);
        RepairTornTail(ControlPath);
        Reload();
        DrainDeferred(filed);
    }

    private void Reload()
    {
        _events.Clear();
        _byId.Clear();
        _sourceIds.Clear();
        _acked.Clear();
        _attempts.Clear();

        foreach (var ev in ReadLines<InboxEvent>(InboxPath))
        {
            if (string.IsNullOrEmpty(ev.Id) || string.IsNullOrEmpty(ev.SourceId) || _byId.ContainsKey(ev.Id))
                continue;
            ev.Mentions ??= new List<string>();
            ev.Scope = InboxContract.RoomScope;
            _events.Add(ev);
            _byId[ev.Id] = ev;
            _sourceIds.Add(ev.SourceId);
        }

        foreach (var line in ReadLines<ControlLine>(ControlPath))
        {
            if (string.IsNullOrEmpty(line.Id) || !_byId.ContainsKey(line.Id))
                continue;
            if (line.Op == "ack")
                _acked.Add(line.Id);
            else if (line.Op == "attempt" && !_acked.Contains(line.Id))
                _attempts[line.Id] = new AttemptState(line.Attempts, line.NextUnix, line.Error ?? "");
        }
    }

    /// <summary>
    /// Move deferred.jsonl into inbox.jsonl. Safe to repeat: source ids already stored are skipped.
    /// The deferred file is truncated only after the inbox appends succeed.
    /// </summary>
    private void DrainDeferred(List<InboxEvent> filed)
    {
        if (!File.Exists(DeferredPath))
            return;
        using var _ = LockFile(DeferredLockPath);
        RepairTornTail(DeferredPath);
        var drafts = ReadLines<InboxEvent>(DeferredPath);
        if (drafts.Count == 0)
        {
            ClearFile(DeferredPath);
            return;
        }

        foreach (var draft in drafts)
        {
            if (string.IsNullOrEmpty(draft.Id) || string.IsNullOrEmpty(draft.SourceId))
                continue;
            if (_byId.ContainsKey(draft.Id) || FindSource(draft.SourceId) is not null)
                continue;
            var stored = Materialize(draft);
            AppendJson(InboxPath, stored);
            Remember(stored);
            filed.Add(stored);
        }

        ClearFile(DeferredPath);
    }

    private void Spill(InboxEvent draft)
    {
        if (string.IsNullOrEmpty(draft.Id) || string.IsNullOrEmpty(draft.SourceId))
            throw new IOException("inbox event needs an id and a source id");
        using var _ = LockFile(DeferredLockPath);
        RepairTornTail(DeferredPath);
        if (File.Exists(DeferredPath))
        {
            foreach (var existing in ReadLines<InboxEvent>(DeferredPath))
            {
                if (string.Equals(existing.Id, draft.Id, StringComparison.Ordinal)
                    || string.Equals(existing.SourceId, draft.SourceId, StringComparison.Ordinal))
                    return;
            }
        }

        AppendJson(DeferredPath, draft);
    }

    private string ControlPath => Path.Combine(_dir, "control.jsonl");
    private string DeferredPath => Path.Combine(_dir, DeferredName);
    private string DeferredLockPath => Path.Combine(_dir, "deferred.lock");

    private T UnderLock<T>(Func<List<InboxEvent>, T> body)
    {
        var filed = new List<InboxEvent>();
        try
        {
            lock (Gate)
            {
                using var stream = Lock();
                RepairAndReload(filed);
                return body(filed);
            }
        }
        finally
        {
            Notify(filed);
        }
    }

    private void Notify(List<InboxEvent> filed)
    {
        if (filed.Count == 0 || OnFiled is null)
            return;
        foreach (var ev in filed)
            OnFiled(ev);
    }

    /// <summary>
    /// Exclusive lock. A holder (the bridge filing a mention, or <c>inbox ack</c>) is normal contention.
    /// Wait out <see cref="_lockBudget"/> instead of failing the write on the first busy open.
    /// </summary>
    private FileStream Lock() => LockFile(Path.Combine(_dir, "inbox.lock"));

    private FileStream LockFile(string path)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        Exception? last = null;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (started.Elapsed >= _lockBudget)
                    throw new IOException(LockBusyMessage, last);
                Thread.Sleep(TimeSpan.FromMilliseconds(20));
            }
        }
    }

    private static bool IsLockBusy(IOException ex) =>
        ex.Message.Contains(LockBusyMessage, StringComparison.Ordinal);

    private static void AppendJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonUtil.Opts);
        File.AppendAllText(path, json + "\n");
    }

    private static void ClearFile(string path)
    {
        if (!File.Exists(path))
            return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        fs.SetLength(0);
        fs.Flush(flushToDisk: true);
    }

    private static List<T> ReadLines<T>(string path)
    {
        var list = new List<T>();
        if (!File.Exists(path))
            return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, JsonUtil.Opts);
                if (item is not null)
                    list.Add(item);
            }
            catch (JsonException)
            {
                // A newline-terminated corrupt line is skipped. An unterminated tail is removed
                // by RepairTornTail before this read, so it cannot glue itself to the next append.
            }
        }

        return list;
    }

    /// <summary>
    /// The last line of a JSONL file must end with a newline. A crash can leave a partial tail.
    /// Leaving that tail in place makes the next append share its line, and reload then skips both.
    /// A complete JSON value that is only missing its newline gets that newline appended.
    /// A partial tail is truncated back to the previous newline. The bytes before that point stay
    /// where they are: this does not rewrite the file, so a crash cannot replace valid history
    /// with a short write.
    /// </summary>
    internal static void RepairTornTail(string path)
    {
        if (!File.Exists(path))
            return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (fs.Length == 0)
            return;
        fs.Seek(-1, SeekOrigin.End);
        if (fs.ReadByte() == '\n')
            return;

        var newlineAt = FindLastNewline(fs);
        var tailStart = newlineAt < 0 ? 0 : newlineAt + 1;
        var tailLength = fs.Length - tailStart;
        if (tailLength <= 0)
            return;

        var complete = false;
        if (tailLength <= int.MaxValue)
        {
            var bytes = new byte[(int)tailLength];
            fs.Position = tailStart;
            fs.ReadExactly(bytes);
            complete = IsCompleteJson(Encoding.UTF8.GetString(bytes));
        }

        if (complete)
        {
            fs.Seek(0, SeekOrigin.End);
            fs.WriteByte((byte)'\n');
        }
        else
        {
            fs.SetLength(tailStart);
        }

        fs.Flush(flushToDisk: true);
    }

    private static long FindLastNewline(FileStream fs)
    {
        var buffer = new byte[8192];
        var pos = fs.Length;
        while (pos > 0)
        {
            var take = (int)Math.Min(buffer.Length, pos);
            pos -= take;
            fs.Position = pos;
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buffer, read, take - read);
                if (n == 0)
                    break;
                read += n;
            }

            for (var i = read - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                    return pos + i;
            }
        }

        return -1;
    }

    private static bool IsCompleteJson(string tail)
    {
        try
        {
            using var doc = JsonDocument.Parse(tail);
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly object Gate = new();

    private sealed record AttemptState(int Count, long NextUnix, string Error);
}

internal sealed class ControlLine
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("at")] public long At { get; set; }
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
    [JsonPropertyName("next_unix")] public long NextUnix { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}
