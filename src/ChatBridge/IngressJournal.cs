using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatBridge;

/// <summary>
/// Append-only journal of fan-out lines retained before the sender inbox is read.
/// A busy sender lock leaves the keep row in place. A finished route appends <c>done</c>.
/// </summary>
internal sealed class IngressJournal
{
    internal const string FileName = "ingress.jsonl";

    private readonly string _path;
    private readonly string _lockPath;
    private readonly TimeSpan _budget;

    public IngressJournal(string path, TimeSpan budget)
    {
        _path = path;
        _lockPath = path + ".lock";
        _budget = budget;
    }

    public void Retain(RoomMessage message, string sourceId)
    {
        using var _ = AgentInbox.AcquireExclusive(_lockPath, _budget);
        AgentInbox.RepairTornTail(_path);
        if (ReadPending().Any(pending => pending.SourceId == sourceId))
            return;
        AgentInbox.AppendDurableJson(_path, Line("keep", message, sourceId));
    }

    public void Forget(string sourceId)
    {
        using var _ = AgentInbox.AcquireExclusive(_lockPath, _budget);
        AgentInbox.RepairTornTail(_path);
        if (!ReadPending().Any(pending => pending.SourceId == sourceId))
            return;
        AgentInbox.AppendDurableJson(_path, new IngressLine { Op = "done", SourceId = sourceId });
    }

    public IReadOnlyList<RetainedIngress> Pending()
    {
        using var _ = AgentInbox.AcquireExclusive(_lockPath, _budget);
        AgentInbox.RepairTornTail(_path);
        return ReadPending();
    }

    private List<RetainedIngress> ReadPending()
    {
        var order = new List<string>();
        var live = new Dictionary<string, RetainedIngress>(StringComparer.Ordinal);
        if (!File.Exists(_path))
            return new List<RetainedIngress>();

        foreach (var raw in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            IngressLine? line;
            try
            {
                line = JsonSerializer.Deserialize<IngressLine>(raw, JsonUtil.Opts);
            }
            catch (JsonException)
            {
                continue;
            }

            if (line is null || string.IsNullOrEmpty(line.SourceId))
                continue;
            if (line.Op == "done")
            {
                live.Remove(line.SourceId);
                continue;
            }

            if (line.Op != "keep")
                continue;
            var message = new RoomMessage(line.Room, line.From, line.Trip, line.Text, line.Ts, line.ServerId);
            if (!string.Equals(MessageIds.Source(message), line.SourceId, StringComparison.Ordinal))
                continue;
            if (!live.ContainsKey(line.SourceId))
                order.Add(line.SourceId);
            live[line.SourceId] = new RetainedIngress(line.SourceId, message);
        }

        var pending = new List<RetainedIngress>(order.Count);
        foreach (var sourceId in order)
        {
            if (live.TryGetValue(sourceId, out var item))
                pending.Add(item);
        }

        return pending;
    }

    private static IngressLine Line(string op, RoomMessage message, string sourceId) => new()
    {
        Op = op,
        SourceId = sourceId,
        Room = message.Room,
        From = message.From,
        Trip = message.Trip,
        Text = message.Text,
        Ts = message.Ts,
        ServerId = message.ServerId
    };

    private sealed class IngressLine
    {
        [JsonPropertyName("op")] public string Op { get; set; } = "";
        [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
        [JsonPropertyName("room")] public string Room { get; set; } = "";
        [JsonPropertyName("from")] public string From { get; set; } = "";
        [JsonPropertyName("trip")] public string? Trip { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("ts")] public long? Ts { get; set; }
        [JsonPropertyName("server_id")] public string? ServerId { get; set; }
    }
}

internal readonly record struct RetainedIngress(string SourceId, RoomMessage Message);
