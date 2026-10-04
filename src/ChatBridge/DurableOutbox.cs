using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBridge;

internal sealed record DurableReply(string Id, string EventId, string To, string Text);

/// <summary>Immutable requests plus fsynced state. Only pending requests are automatic sends.
/// In-flight requests survive a crash as uncertain; v1 cannot prove exactly-once delivery.</summary>
internal sealed class DurableOutbox
{
    private readonly string _root;
    private readonly object _gate = new();
    private static readonly Regex IdPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> States = new() { "pending", "sending", "sent", "echo_observed", "uncertain", "dropped" };

    public DurableOutbox(string baseDir, bool recover = true)
    {
        _root = Path.Combine(baseDir, "durable-outbox");
        if (new DirectoryInfo(_root).LinkTarget is not null)
            throw new ConfigException("durable outbox directory is a symlink");
        Directory.CreateDirectory(_root);
        if (recover)
            foreach (var row in Requests())
                if (State(row.Id) == "sending") WriteState(row.Id, "uncertain");
    }

    private string StatePath(string id) => Path.Combine(_root, id + ".state.json");
    private IEnumerable<DurableReply> Requests()
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IdPattern.IsMatch(id)) continue;
            if (new FileInfo(path).LinkTarget is not null) throw new ConfigException("durable outbox request is a symlink");
            var row = JsonSerializer.Deserialize<DurableReply>(File.ReadAllText(path), JsonUtil.Opts);
            if (row is null || row.Id != id || string.IsNullOrWhiteSpace(row.EventId) ||
                string.IsNullOrWhiteSpace(row.To) || string.IsNullOrWhiteSpace(row.Text) || row.Text.Length > 4000)
                throw new ConfigException("invalid durable outbox request");
            yield return row;
        }
    }

    internal string State(string id)
    {
        if (!IdPattern.IsMatch(id)) throw new ArgumentException("outbox id must be 64 lowercase hex characters");
        var path = StatePath(id);
        if (!File.Exists(path)) return "pending";
        if (new FileInfo(path).LinkTarget is not null) throw new ConfigException("durable outbox state is a symlink");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var state = doc.RootElement.GetProperty("state").GetString();
        if (state is null || !States.Contains(state)) throw new ConfigException("invalid durable outbox state");
        return state;
    }

    private void WriteState(string id, string state)
    {
        var path = StatePath(id);
        var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new { state, at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        Posix.SyncDirectory(_root);
    }

    public IReadOnlyList<DurableReply> Pending()
    {
        lock (_gate) return Requests().Where(r => State(r.Id) == "pending").ToList();
    }

    public void BeginSend(string id) { lock (_gate) WriteState(id, "sending"); }
    public void Sent(string id)
    {
        lock (_gate) if (State(id) == "sending") WriteState(id, "sent");
    }
    public void Uncertain(string id)
    {
        lock (_gate) if (State(id) != "echo_observed") WriteState(id, "uncertain");
    }

    // v1 has no correlated receipt. This is text/nick echo evidence, never task completion.
    public void ObserveEcho(string? nick, string? text, string ownNick)
    {
        if (nick != ownNick || text is null) return;
        lock (_gate)
        {
            var row = Requests().FirstOrDefault(r => r.Text == text && State(r.Id) is "sending" or "sent" or "uncertain");
            if (row is not null) WriteState(row.Id, "echo_observed");
        }
    }

    public object[] Status()
    {
        lock (_gate) return Requests().Select(r => (object)new { id = r.Id, state = State(r.Id) }).ToArray();
    }

    public void Resolve(string id, string action)
    {
        lock (_gate)
        {
            if (!Requests().Any(r => r.Id == id)) throw new ArgumentException("outbox id not found");
            var state = State(id);
            if (state is not ("sending" or "sent" or "uncertain")) throw new ArgumentException("only an uncertain or unconfirmed send can be resolved");
            WriteState(id, action == "requeue" ? "pending" : action == "drop" ? "dropped" : throw new ArgumentException("resolve action must be requeue or drop"));
        }
    }
}
