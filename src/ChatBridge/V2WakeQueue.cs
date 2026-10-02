using System.Text;
using System.Text.Json.Nodes;

namespace ChatBridge;

/// <summary>One inbound v2 payload the wake consumers can store. No owner secret.</summary>
internal sealed record V2QueuedDelivery(
    string DeliveryId,
    long Generation,
    string Text,
    string From,
    string? Trip,
    long? Ts,
    string? MessageId);

/// <summary>
/// Durable enqueue of a v2 delivery into the queues agents already read.
/// Return false to refuse the wire ack. A second call for the same delivery id is success
/// when the first enqueue is still on disk. <paramref name="error"/> is a short token, never the payload.
/// </summary>
internal interface IV2ConsumerQueue
{
    bool TryEnqueue(V2QueuedDelivery delivery, out string? error);
}

/// <summary>
/// Copies the message fields a consumer can queue. §11 does not pin the delivery body.
/// Flat <c>text</c> / <c>nick</c> / <c>from</c> / <c>trip</c> / <c>ts</c> / <c>id</c> are kept.
/// The same fields are read from a nested <c>message</c>, <c>msg</c>, <c>chat</c>, or <c>payload</c>
/// object when that object is what carries <c>text</c>. Owner fields are not copied.
/// </summary>
internal static class V2Payload
{
    public static JsonObject Copy(JsonObject frame)
    {
        var source = frame;
        foreach (var key in new[] { "message", "msg", "chat", "payload" })
        {
            if (frame[key] is JsonObject nested && !string.IsNullOrWhiteSpace(Json.Str(nested, "text")))
            {
                source = nested;
                break;
            }
        }

        var payload = new JsonObject();
        var text = Json.Str(source, "text") ?? Json.Str(frame, "text");
        if (!string.IsNullOrWhiteSpace(text))
            payload["text"] = text;
        var nick = Json.Str(source, "nick") ?? Json.Str(source, "from")
            ?? Json.Str(frame, "nick") ?? Json.Str(frame, "from");
        if (!string.IsNullOrEmpty(nick))
            payload["nick"] = nick;
        var trip = Json.Str(source, "trip") ?? Json.Str(frame, "trip");
        if (!string.IsNullOrEmpty(trip))
            payload["trip"] = trip;
        var ts = JsonNum.Long(source, "ts") ?? JsonNum.Long(source, "time")
            ?? JsonNum.Long(frame, "ts") ?? JsonNum.Long(frame, "time");
        if (ts is not null)
            payload["ts"] = ts.Value;
        var id = Json.Str(source, "id") ?? Json.Str(source, "messageId")
            ?? Json.Str(frame, "id") ?? Json.Str(frame, "messageId");
        if (!string.IsNullOrEmpty(id))
            payload["id"] = id;
        return payload;
    }

    public static V2QueuedDelivery? TryQueue(string deliveryId, long generation, JsonObject? payload)
    {
        var text = payload is null ? null : Json.Str(payload, "text");
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return new V2QueuedDelivery(
            deliveryId,
            generation,
            text,
            payload is null ? "" : Json.Str(payload, "nick") ?? "",
            payload is null ? null : Json.Str(payload, "trip"),
            payload is null ? null : JsonNum.Long(payload, "ts"),
            payload is null ? null : Json.Str(payload, "id"));
    }
}

/// <summary>
/// Before a v2 ack: fsync the payload into <c>agents/&lt;id&gt;/inbox.jsonl</c> (the
/// <c>inbox due</c> wake) and a <c>v2_handoff</c> chat line on the room <c>inbox.jsonl</c>
/// (<c>mention_hook</c>, <c>watch</c>, and <c>hook</c>). The agent-inbox source id is
/// <see cref="MessageIds.Overlap(string, V2QueuedDelivery)"/>, the same key a v1 chat of
/// that message already uses, so the second copy is a duplicate. The room line keeps the
/// room message id when the delivery has one, and does not use the lease id as that id.
/// v1 with the flag off never constructs this. A busy agent-inbox lock spills to
/// <c>deferred.jsonl</c> and still counts as durable.
/// </summary>
internal sealed class V2WakeQueue : IV2ConsumerQueue
{
    private readonly string _inboxPath;
    private readonly string _agentsRoot;
    private readonly string _room;
    private readonly string? _agentId;
    private readonly object _fileLock;

    public V2WakeQueue(RelayConfig cfg, object fileLock)
    {
        _inboxPath = Path.Combine(cfg.BaseDir, "inbox.jsonl");
        _agentsRoot = Path.Combine(cfg.BaseDir, "agents");
        _room = cfg.Channel;
        _agentId = ChooseAgent(cfg);
        _fileLock = fileLock;
    }

    public string? AgentId => _agentId;

    /// <summary>
    /// Configured agent that owns the bridge nick, otherwise the only configured agent.
    /// When the roster is empty and the nick is a safe id, that nick is the directory name.
    /// Several configured agents and no nick match: no per-agent file (the room line is the queue).
    /// </summary>
    public static string? ChooseAgent(RelayConfig cfg)
    {
        var owned = cfg.Roster.FindByNick(cfg.Nick);
        if (owned is not null)
            return owned.Id;
        if (cfg.Roster.Agents.Count == 1)
            return cfg.Roster.Agents[0].Id;
        if (cfg.Roster.Agents.Count == 0 && AgentPath.IsSafeId(cfg.Nick))
            return cfg.Nick;
        return null;
    }

    public bool TryEnqueue(V2QueuedDelivery delivery, out string? error)
    {
        error = null;
        try
        {
            if (_agentId is not null && !FileAgent(delivery))
            {
                error = "rejected";
                return false;
            }

            AppendRoom(delivery);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.GetType().Name;
            return false;
        }
    }

    private bool FileAgent(V2QueuedDelivery delivery)
    {
        var inbox = AgentInbox.Open(Path.Combine(_agentsRoot, _agentId!));
        inbox.OnFiled = new ConversationStore(_agentsRoot).AppendRoom;
        var sourceId = MessageIds.Overlap(_room, delivery);
        var write = inbox.Write(new InboxEvent
        {
            Id = MessageIds.Delivery(_agentId!, sourceId),
            Agent = _agentId!,
            SourceId = sourceId,
            Room = _room,
            From = delivery.From,
            Trip = string.IsNullOrWhiteSpace(delivery.Trip) ? null : delivery.Trip,
            Text = delivery.Text,
            Mentions = new List<string>(),
            Scope = InboxContract.RoomScope,
            Ts = delivery.Ts
        });
        return write.Kind is InboxWriteKind.Stored or InboxWriteKind.Duplicate or InboxWriteKind.Deferred;
    }

    private void AppendRoom(V2QueuedDelivery delivery)
    {
        var msg = new JsonObject
        {
            ["cmd"] = "chat",
            ["type"] = "chat",
            ["nick"] = delivery.From,
            ["text"] = delivery.Text
        };
        // The lease id is not the room message id. Copy the message id the v1 chat
        // already carries so mention_hook's dedup key matches. No id: the hook hashes
        // the same content fields a v1 chat without an id uses.
        if (!string.IsNullOrWhiteSpace(delivery.MessageId))
        {
            var messageId = delivery.MessageId.Trim();
            msg["id"] = messageId;
            msg["messageId"] = messageId;
        }
        if (!string.IsNullOrEmpty(delivery.Trip))
            msg["trip"] = delivery.Trip;
        if (delivery.Ts is { } ts)
            msg["ts"] = ts;
        var row = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["dir"] = "in",
            ["v2_handoff"] = delivery.DeliveryId,
            ["msg"] = msg
        };
        var bytes = Encoding.UTF8.GetBytes(row.ToJsonString(JsonUtil.Opts) + "\n");
        lock (_fileLock)
        {
            var dir = Path.GetDirectoryName(_inboxPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            using var fs = new FileStream(_inboxPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
    }
}
