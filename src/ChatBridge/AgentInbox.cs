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
    [JsonPropertyName("ts")] public long? Ts { get; set; }
}

internal sealed record InboxView(InboxEvent Event, int Attempts, long? NextUnix, string? Error)
{
    public bool IsDue(DateTimeOffset now) => NextUnix is null || NextUnix <= now.ToUnixTimeSeconds();
}

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
/// control.jsonl, so a restart reloads both and continues in seq order.
/// </summary>
internal sealed class AgentInbox
{
    public static readonly TimeSpan DefaultLockBudget = TimeSpan.FromSeconds(2);

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

    public static AgentInbox Open(string agentDir, TimeSpan? lockBudget = null)
    {
        Directory.CreateDirectory(agentDir);
        var inbox = new AgentInbox(agentDir, lockBudget ?? DefaultLockBudget);
        inbox.Reload();
        return inbox;
    }

    public bool TryAppend(InboxEvent draft, out InboxEvent stored)
    {
        lock (Gate)
        {
            using var _ = Lock();
            RepairAndReload();
            if (_byId.TryGetValue(draft.Id, out var existing))
            {
                stored = existing;
                return false;
            }

            var bySource = FindSource(draft.SourceId);
            if (bySource is not null)
            {
                stored = bySource;
                return false;
            }

            var seq = _events.Count == 0 ? 1 : _events.Max(e => e.Seq) + 1;
            stored = new InboxEvent
            {
                Id = draft.Id,
                Seq = seq,
                Agent = draft.Agent,
                SourceId = draft.SourceId,
                Room = draft.Room,
                From = draft.From,
                Trip = draft.Trip,
                Text = draft.Text,
                Mentions = draft.Mentions.ToList(),
                Scope = InboxContract.RoomScope,
                Hop = draft.Hop,
                Fanout = draft.Fanout,
                Ts = draft.Ts
            };
            AppendJson(InboxPath, stored);
            _events.Add(stored);
            _byId[stored.Id] = stored;
            _sourceIds.Add(stored.SourceId);
            return true;
        }
    }

    public IReadOnlyList<InboxView> Pending()
    {
        lock (Gate)
        {
            using var _ = Lock();
            RepairAndReload();
            return Ordered().ToList();
        }
    }

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

    public bool Ack(string id, DateTimeOffset now)
    {
        lock (Gate)
        {
            using var _ = Lock();
            RepairAndReload();
            if (!_byId.ContainsKey(id))
                return false;
            if (_acked.Contains(id))
                return true;
            AppendJson(ControlPath, new ControlLine { Op = "ack", Id = id, At = now.ToUnixTimeSeconds() });
            _acked.Add(id);
            return true;
        }
    }

    public bool RecordFailure(string id, string error, DateTimeOffset now)
    {
        lock (Gate)
        {
            using var _ = Lock();
            RepairAndReload();
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
        }
    }

    private IEnumerable<InboxView> Ordered() =>
        _events
            .Where(e => !_acked.Contains(e.Id))
            .OrderBy(e => e.Seq)
            .Select(e =>
            {
                var attempt = _attempts.GetValueOrDefault(e.Id);
                return new InboxView(e, attempt?.Count ?? 0, attempt?.NextUnix, attempt?.Error);
            });

    /// <summary>Highest <c>hop</c> on a fan-out row in this inbox, acked or not. Zero when there is none.</summary>
    public int HighestFanoutHop()
    {
        lock (Gate)
        {
            using var _ = Lock();
            RepairAndReload();
            var max = 0;
            foreach (var ev in _events)
            {
                if (ev.Fanout && ev.Hop > max)
                    max = ev.Hop;
            }

            return max;
        }
    }

    private InboxEvent? FindSource(string sourceId) =>
        _events.FirstOrDefault(e => string.Equals(e.SourceId, sourceId, StringComparison.Ordinal));

    private void RepairAndReload()
    {
        RepairTornTail(InboxPath);
        RepairTornTail(ControlPath);
        Reload();
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

    private string ControlPath => Path.Combine(_dir, "control.jsonl");

    /// <summary>
    /// Exclusive lock. A holder (the bridge filing a mention, or <c>inbox ack</c>) is normal contention.
    /// Wait out <see cref="_lockBudget"/> instead of failing the write on the first busy open.
    /// </summary>
    private FileStream Lock()
    {
        var path = Path.Combine(_dir, "inbox.lock");
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
                    throw new IOException("inbox lock busy", last);
                Thread.Sleep(TimeSpan.FromMilliseconds(20));
            }
        }
    }

    private static void AppendJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonUtil.Opts);
        File.AppendAllText(path, json + "\n");
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
    /// A partial tail is cut back to the previous newline. A complete JSON value that is only missing
    /// its newline is terminated.
    /// </summary>
    private static void RepairTornTail(string path)
    {
        if (!File.Exists(path))
            return;
        var text = File.ReadAllText(path);
        if (text.Length == 0 || text[^1] == '\n')
            return;

        var nl = text.LastIndexOf('\n');
        var tail = nl < 0 ? text : text[(nl + 1)..];
        if (tail.Length == 0)
            return;

        if (IsCompleteJson(tail))
            File.WriteAllText(path, text + "\n");
        else
            File.WriteAllText(path, nl < 0 ? "" : text[..(nl + 1)]);
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
