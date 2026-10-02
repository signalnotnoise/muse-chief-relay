using System.Text;
using System.Text.Json.Nodes;

namespace ChatBridge;

/// <summary>
/// Which wire dialect a connection will speak. v1 is the default. Deployed is the
/// opt-in path and follows contract §11, not the designed frames in §§2–9.
/// </summary>
internal enum V2Dialect
{
    V1 = 0,
    Deployed = 1
}

/// <summary>Join pin chosen from one hello. <see cref="Note"/> is safe to log (no secrets).</summary>
internal sealed record V2Plan(int JoinVersion, V2Dialect Dialect, string? Note)
{
    public static V2Plan StayOnV1 { get; } = new(1, V2Dialect.V1, null);
}

/// <summary>
/// Reads a voizle hello and decides the join pin. §§2–9 dual-hello frames are recognized
/// so they are not half-negotiated. §11 is what a selected v2 join actually speaks.
/// </summary>
internal static class V2Negotiation
{
    private static readonly string[] DesignedFeatures = ["inbox", "lease", "resume"];

    public static V2Plan Decide(JsonObject hello, bool optIn)
    {
        var deployed = IsDeployedDurableHello(hello);
        var designed = DesignedHello(hello);

        if (!optIn)
        {
            if (deployed || designed != DesignedHelloKind.Absent)
            {
                return new V2Plan(1, V2Dialect.V1,
                    "server advertised v2; protocol_v2 is off, so this connection stays v1");
            }

            return V2Plan.StayOnV1;
        }

        // §11: production hello is v:1 plus durable:true and durableVersion:2.
        // A versions array, when present, is future work. Prefer the deployed dialect.
        if (deployed)
        {
            var note = designed switch
            {
                DesignedHelloKind.Complete =>
                    "hello also has versions:[2,1]; §11 has no versions array. Pinning v2 on the deployed dialect, not designed bind/resume/binding frames.",
                DesignedHelloKind.Incomplete =>
                    "hello versions or v2.features are incomplete. Ignoring them and pinning v2 on the deployed durable advertisement.",
                _ => (string?)null
            };
            return new V2Plan(2, V2Dialect.Deployed, note);
        }

        if (designed == DesignedHelloKind.Complete)
        {
            return new V2Plan(1, V2Dialect.V1,
                "designed dual hello (versions:[2,1] with inbox, lease, resume) is future work per §11. Not half-negotiated; staying on v1.");
        }

        if (designed == DesignedHelloKind.Incomplete)
        {
            return new V2Plan(1, V2Dialect.V1,
                "v2 advertisement is incomplete (durableVersion is not 2, or designed features are missing). Staying on v1.");
        }

        return V2Plan.StayOnV1;
    }

    /// <summary>§11 deployed hello: protocol voizle-text-relay, v 1, durable true, durableVersion 2.</summary>
    public static bool IsDeployedDurableHello(JsonObject hello)
    {
        if (!string.Equals(Json.Str(hello, "protocol"), "voizle-text-relay", StringComparison.Ordinal))
            return false;
        if (JsonNum.Long(hello, "v") != 1)
            return false;
        if (JsonNum.Bool(hello, "durable") != true)
            return false;
        return JsonNum.Long(hello, "durableVersion") == 2;
    }

    private static DesignedHelloKind DesignedHello(JsonObject hello)
    {
        if (hello["versions"] is not JsonArray versions)
            return DesignedHelloKind.Absent;
        var speaks2 = false;
        foreach (var node in versions)
        {
            if (node is JsonValue value && JsonNum.FromValue(value) == 2)
                speaks2 = true;
        }

        if (!speaks2)
            return DesignedHelloKind.Absent;

        if (hello["v2"] is not JsonObject v2 || v2["features"] is not JsonArray features)
            return DesignedHelloKind.Incomplete;

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in features)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var feature) && feature is not null)
                present.Add(feature);
        }

        foreach (var required in DesignedFeatures)
        {
            if (!present.Contains(required))
                return DesignedHelloKind.Incomplete;
        }

        return DesignedHelloKind.Complete;
    }

    private enum DesignedHelloKind
    {
        Absent = 0,
        Incomplete = 1,
        Complete = 2
    }
}

/// <summary>One step of the durable outbound pump.</summary>
internal readonly record struct V2ChatStep(bool Hold, string? ClientMsgId, JsonObject? Frame)
{
    public static V2ChatStep Idle { get; } = new(false, null, null);

    public bool Send => Frame is not null;
}

/// <summary>Frames the socket loop should write, plus whether the outbound pump should wake.</summary>
internal readonly record struct V2InboundResult(IReadOnlyList<JsonObject> Send, bool WakeOutbox, string? EndSession)
{
    public static V2InboundResult None { get; } = new(Array.Empty<JsonObject>(), false, null);
}

/// <summary>Ends a confirmed v2 session so the normal reconnect backoff runs. Not a process exit.</summary>
internal sealed class V2SessionEndException(string message) : Exception(message);

/// <summary>
/// Opt-in v2 client state against contract §11. The bridge constructs this only when
/// <see cref="RelayConfig.ProtocolV2"/> is true. v1 sessions never touch these files.
/// </summary>
/// <remarks>
/// Outbound: a line is fsynced as <c>queued</c> before it can be sent, then fsynced as
/// <c>sent</c> before the socket write. <c>accepted</c> is the only completion. A <c>sent</c>
/// row with no <c>accepted</c> is uncertain: the pump holds and does not resend it, because
/// §11 says a retry after a missed <c>accepted</c> creates a second message. The local id is
/// <c>client_msg_id</c>. It is not put on the deployed chat frame — the server has no
/// idempotency key.
/// Inbound: a delivery is fsynced as <c>seen</c> and then <c>ack_pending</c> before the ack
/// frame is returned. Completion is <c>ack_result</c> <c>processed</c> or <c>idempotent</c>
/// for that lease generation. A seen or fenced row is not complete and may be re-leased.
/// </remarks>
internal sealed class V2Client
{
    public const string OutboundName = "durable-v2-outbound.jsonl";
    public const string InboundName = "durable-v2-inbound.jsonl";
    public const int MaxPullsPerSession = 100;

    private readonly object _gate = new();
    private readonly string _outboundPath;
    private readonly string _inboundPath;
    private readonly List<string> _outOrder = new();
    private readonly Dictionary<string, OutItem> _out = new();
    private readonly List<string> _inOrder = new();
    private readonly Dictionary<string, InItem> _in = new();
    private readonly Queue<string> _notes = new();
    private readonly Dictionary<string, int> _seenEpoch = new();

    private string? _outboxPath;
    private long _cursor;
    private bool _cursorSet;
    private int _epoch;
    private bool _inboxAuth;
    private bool _initialPullSent;
    private bool _pullAgain;
    private int _pulls;
    private string? _ackInFlight;

    private V2Client(string baseDir)
    {
        _outboundPath = Path.Combine(baseDir, OutboundName);
        _inboundPath = Path.Combine(baseDir, InboundName);
        Load(_outboundPath, ApplyOut);
        Load(_inboundPath, ApplyIn);
    }

    public static V2Client Open(string baseDir) => new(baseDir);

    public string OutboundPath => _outboundPath;
    public string InboundPath => _inboundPath;

    /// <summary>
    /// Call once per connection after the hello pins the deployed dialect.
    /// The first call snapshots the outbox offset so historical lines are not replayed.
    /// Later calls (including after a restart) keep that offset and the queue head.
    /// </summary>
    public void BeginDeployed(string outboxPath)
    {
        lock (_gate)
        {
            _outboxPath = outboxPath;
            _epoch++;
            _inboxAuth = false;
            _initialPullSent = false;
            _pullAgain = false;
            _pulls = 0;
            _ackInFlight = null;
            _seenEpoch.Clear();
            if (_cursorSet)
                return;
            // Snapshot after the last complete line. A tail with no newline is still
            // being written; starting at raw EOF would skip its head and never send it.
            var length = CompleteLineEnd(outboxPath);
            AppendOps(_outboundPath, new JsonObject[] { CursorOp(length) });
            _cursor = length;
            _cursorSet = true;
        }
    }

    public IReadOnlyList<string> TakeNotes()
    {
        lock (_gate)
        {
            if (_notes.Count == 0)
                return Array.Empty<string>();
            var notes = _notes.ToArray();
            _notes.Clear();
            return notes;
        }
    }

    /// <summary>Fsync new outbox lines as <c>queued</c> before any of them can be sent.</summary>
    public void ImportNewOutboxLines()
    {
        lock (_gate)
            ImportLocked();
    }

    /// <summary>Local chat (auto-ack) joins the same durable queue. It does not skip the head.</summary>
    public string EnqueueLocal(string text)
    {
        lock (_gate)
        {
            var id = NewId();
            var op = new JsonObject
            {
                ["op"] = "enqueue",
                ["client_msg_id"] = id,
                ["text"] = text
            };
            AppendOps(_outboundPath, new[] { op });
            ApplyOut(op);
            return id;
        }
    }

    /// <summary>
    /// Returns the next chat, or a hold when the head was handed to the socket and no
    /// <c>accepted</c> has been observed. The <c>sent</c> row is fsynced before the frame is returned.
    /// </summary>
    public V2ChatStep NextChat()
    {
        lock (_gate)
        {
            ImportLocked();
            var head = FirstOpen();
            if (head is null)
                return V2ChatStep.Idle;
            if (head.State == "sent")
                return new V2ChatStep(true, head.Id, null);
            if (head.State != "queued")
                return V2ChatStep.Idle;

            var sent = new JsonObject { ["op"] = "sent", ["client_msg_id"] = head.Id };
            AppendOps(_outboundPath, new[] { sent });
            ApplyOut(sent);
            return new V2ChatStep(false, head.Id, ChatFrame(head.Text));
        }
    }

    /// <summary>
    /// Operator or test hook. <paramref name="decision"/> is <c>requeue</c> (may duplicate on the
    /// server) or <c>drop</c> (do not send). Only a <c>sent</c> row can be resolved.
    /// </summary>
    public bool ResolveUncertain(string clientMsgId, string decision)
    {
        if (decision is not ("requeue" or "drop"))
            return false;
        lock (_gate)
        {
            if (!_out.TryGetValue(clientMsgId, out var item) || item.State != "sent")
                return false;
            var op = new JsonObject
            {
                ["op"] = "resolve",
                ["client_msg_id"] = clientMsgId,
                ["decision"] = decision
            };
            AppendOps(_outboundPath, new[] { op });
            ApplyOut(op);
            return true;
        }
    }

    public V2InboundResult OnFrame(JsonObject frame)
    {
        lock (_gate)
        {
            var type = Json.Str(frame, "type") ?? Json.Str(frame, "cmd");
            switch (type)
            {
                case "welcome":
                    return OnWelcome(frame);
                case "delivery":
                    return OnDelivery(frame);
                case "ack_result":
                    return OnAckResult(frame);
                case "error":
                    return OnError(frame);
                case "accepted":
                    return OnAccepted(frame);
                case "pull_result":
                    return OnPullResult(frame);
                case "dead":
                    return OnDead(frame);
                case "bound":
                case "leased":
                case "acked":
                case "resumed":
                    // Designed §§2–9 frames. Do not treat them as ownership or completion.
                    _notes.Enqueue($"ignored designed v2 frame '{type}' (§11 future work)");
                    return V2InboundResult.None;
                default:
                    return V2InboundResult.None;
            }
        }
    }

    /// <summary>Deployed chat frame. No <c>client_msg_id</c> — §11 has no server idempotency key.</summary>
    public static JsonObject ChatFrame(string text) => new()
    {
        ["v"] = 2,
        ["type"] = "chat",
        ["text"] = text
    };

    public static JsonObject JoinFrame(string room, string nick, string? trip, string? ownerSecret)
    {
        var join = new JsonObject
        {
            ["v"] = 2,
            ["type"] = "join",
            ["room"] = room,
            ["nick"] = nick
        };
        if (trip is not null)
            join["trip"] = trip;
        // §11 ownership is the client-chosen secret, not the public trip and not a binding token.
        if (!string.IsNullOrEmpty(ownerSecret))
            join["pass"] = ownerSecret;
        return join;
    }

    public static JsonObject PullFrame() => new() { ["v"] = 2, ["type"] = "pull" };

    public static JsonObject AckFrame(string deliveryId, long generation) => new()
    {
        ["v"] = 2,
        ["type"] = "ack",
        ["deliveryId"] = deliveryId,
        ["leaseGeneration"] = generation
    };

    private V2InboundResult OnWelcome(JsonObject frame)
    {
        _inboxAuth = JsonNum.Bool(frame, "inboxAuth") == true;
        var send = new List<JsonObject>();
        if (TryArmAck(replayOnly: true) is { } ack)
            send.Add(ack);
        else if (_inboxAuth && ArmPull() is { } pull)
            send.Add(pull);
        return new V2InboundResult(send, false, null);
    }

    private V2InboundResult OnDelivery(JsonObject frame)
    {
        var id = Json.Str(frame, "deliveryId");
        var generation = JsonNum.Long(frame, "leaseGeneration");
        if (string.IsNullOrEmpty(id) || generation is null)
        {
            _notes.Enqueue("delivery missing deliveryId or leaseGeneration; not acked (unresolved frame)");
            return V2InboundResult.None;
        }

        if (_in.TryGetValue(id, out var existing))
        {
            if (existing.Generation == generation && existing.State is "processed" or "dead")
                return V2InboundResult.None;
            if (existing.Generation == generation && existing.State == "ack_pending")
                return V2InboundResult.None;
            // Same generation, still only seen: a re-lease. Not complete. Fall through and ack if idle.
            // A higher generation is a new lease even when an older one was processed.
        }

        var seen = new JsonObject
        {
            ["op"] = "seen",
            ["deliveryId"] = id,
            ["leaseGeneration"] = generation.Value
        };
        AppendOps(_inboundPath, new[] { seen });
        ApplyIn(seen);
        // A newer lease that arrives while this id's ack is still in flight is stored
        // on the row and armed only after that ack settles. Overwriting now would
        // let the in-flight result complete the wrong generation.
        if (_in.TryGetValue(id, out var stored) && stored.State == "seen")
            _seenEpoch[id] = _epoch;

        var send = new List<JsonObject>();
        if (TryArmAck(replayOnly: false) is { } ack)
            send.Add(ack);
        return new V2InboundResult(send, false, null);
    }

    private V2InboundResult OnAckResult(JsonObject frame)
    {
        var state = Json.Str(frame, "state");
        var idempotent = JsonNum.Bool(frame, "idempotent") == true;
        if (state == "processed" || idempotent)
            CompleteInFlight(idempotent ? "idempotent" : "processed");
        else if (state is "lease_fenced" or "lease_expired")
            FenceInFlight(state);
        else
        {
            _notes.Enqueue("ack_result did not confirm completion");
            return V2InboundResult.None;
        }

        return ContinueAfterAck();
    }

    private V2InboundResult OnError(JsonObject frame)
    {
        var code = Json.Str(frame, "code");
        if (code is "lease_fenced" or "lease_expired")
        {
            FenceInFlight(code);
            return ContinueAfterAck();
        }

        if (code == "inbox_held")
        {
            // Ownership is the owner secret presented at join. Do not retry with a different
            // trip, and do not read a binding token. The caller backs off the session.
            return new V2InboundResult(Array.Empty<JsonObject>(), false, "inbox_held");
        }

        return V2InboundResult.None;
    }

    private V2InboundResult OnAccepted(JsonObject frame)
    {
        var head = FirstOpen();
        if (head is not { State: "sent" })
        {
            _notes.Enqueue("accepted with no in-flight send; not applied");
            return V2InboundResult.None;
        }

        var op = new JsonObject
        {
            ["op"] = "accepted",
            ["client_msg_id"] = head.Id
        };
        if (Json.Str(frame, "messageId") is { } messageId)
            op["messageId"] = messageId;
        if (Json.Str(frame, "ingressId") is { } ingressId)
            op["ingressId"] = ingressId;
        AppendOps(_outboundPath, new[] { op });
        ApplyOut(op);
        return new V2InboundResult(Array.Empty<JsonObject>(), true, null);
    }

    private V2InboundResult OnPullResult(JsonObject frame)
    {
        var queued = JsonNum.Long(frame, "queued") ?? 0;
        if (queued > 0)
            _pullAgain = true;
        if (_ackInFlight is not null)
            return V2InboundResult.None;
        if (!_pullAgain)
            return V2InboundResult.None;
        return ArmPull() is { } pull
            ? new V2InboundResult(new[] { pull }, false, null)
            : V2InboundResult.None;
    }

    private V2InboundResult OnDead(JsonObject frame)
    {
        var id = Json.Str(frame, "deliveryId");
        if (string.IsNullOrEmpty(id))
        {
            _notes.Enqueue("dead frame has no deliveryId (unresolved); not acked");
            return V2InboundResult.None;
        }

        var generation = JsonNum.Long(frame, "leaseGeneration")
            ?? (_in.TryGetValue(id, out var existing) ? existing.Generation : 0);
        var op = new JsonObject
        {
            ["op"] = "dead",
            ["deliveryId"] = id,
            ["leaseGeneration"] = generation
        };
        AppendOps(_inboundPath, new[] { op });
        ApplyIn(op);
        _seenEpoch.Remove(id);
        if (_ackInFlight == id)
            _ackInFlight = null;
        return V2InboundResult.None;
    }

    private V2InboundResult ContinueAfterAck()
    {
        var send = new List<JsonObject>();
        if (TryArmAck(replayOnly: false) is { } ack)
            send.Add(ack);
        else if ((_pullAgain || (_inboxAuth && !_initialPullSent)) && ArmPull() is { } pull)
            send.Add(pull);
        return new V2InboundResult(send, true, null);
    }

    private JsonObject? TryArmAck(bool replayOnly)
    {
        if (_ackInFlight is not null)
            return null;

        foreach (var id in _inOrder)
        {
            var item = _in[id];
            if (item.State == "ack_pending")
                return WriteAck(item);
        }

        if (replayOnly)
            return null;

        foreach (var id in _inOrder)
        {
            var item = _in[id];
            if (item.State != "seen")
                continue;
            if (!_seenEpoch.TryGetValue(id, out var epoch) || epoch != _epoch)
                continue;
            return WriteAck(item);
        }

        return null;
    }

    private JsonObject WriteAck(InItem item)
    {
        var op = new JsonObject
        {
            ["op"] = "ack_pending",
            ["deliveryId"] = item.DeliveryId,
            ["leaseGeneration"] = item.Generation
        };
        AppendOps(_inboundPath, new[] { op });
        ApplyIn(op);
        _ackInFlight = item.DeliveryId;
        _seenEpoch.Remove(item.DeliveryId);
        return AckFrame(item.DeliveryId, item.Generation);
    }

    private void CompleteInFlight(string how)
    {
        var id = _ackInFlight ?? Oldest(static item => item.State == "ack_pending");
        _ackInFlight = null;
        if (id is null || !_in.TryGetValue(id, out var item))
            return;
        if (item.State is "processed" or "dead")
            return;
        var op = new JsonObject
        {
            ["op"] = "processed",
            ["deliveryId"] = id,
            ["leaseGeneration"] = item.Generation,
            ["how"] = how
        };
        AppendOps(_inboundPath, new[] { op });
        ApplyIn(op);
        _seenEpoch.Remove(id);
        PromoteQueuedLease(item);
    }

    private void FenceInFlight(string code)
    {
        var id = _ackInFlight ?? Oldest(static item => item.State == "ack_pending");
        _ackInFlight = null;
        if (id is null || !_in.TryGetValue(id, out var item))
            return;
        if (item.State is "processed" or "dead")
            return;
        var op = new JsonObject
        {
            ["op"] = "fence",
            ["deliveryId"] = id,
            ["leaseGeneration"] = item.Generation,
            ["code"] = code
        };
        AppendOps(_inboundPath, new[] { op });
        ApplyIn(op);
        // Drop the epoch so Continue does not immediately ack the same generation again.
        _seenEpoch.Remove(id);
        PromoteQueuedLease(item);
    }

    /// <summary>
    /// A delivery that arrived during an in-flight ack was stored as
    /// <see cref="InItem.QueuedGeneration"/>. Once that ack settles, the newer
    /// generation becomes <c>seen</c> so it can be acked. It is not marked complete.
    /// </summary>
    private void PromoteQueuedLease(InItem item)
    {
        if (item.QueuedGeneration is not { } next || next == item.Generation)
            return;
        if (item.State == "processed" && next < item.Generation)
            return;
        var seen = new JsonObject
        {
            ["op"] = "seen",
            ["deliveryId"] = item.DeliveryId,
            ["leaseGeneration"] = next
        };
        AppendOps(_inboundPath, new[] { seen });
        ApplyIn(seen);
        if (item.State == "seen")
            _seenEpoch[item.DeliveryId] = _epoch;
    }

    private JsonObject? ArmPull()
    {
        _pullAgain = false;
        _initialPullSent = true;
        if (_pulls >= MaxPullsPerSession)
        {
            _notes.Enqueue("pull cap reached; not paging further this session");
            return null;
        }

        _pulls++;
        return PullFrame();
    }

    private string? Oldest(Func<InItem, bool> match)
    {
        foreach (var id in _inOrder)
        {
            if (match(_in[id]))
                return id;
        }

        return null;
    }

    private void ImportLocked()
    {
        if (!_cursorSet || _outboxPath is null || !File.Exists(_outboxPath))
            return;

        var reader = new OutboxReader(_outboxPath, _cursor);
        var lines = reader.ReadPending();
        if (lines.Count == 0)
            return;

        var ops = new List<JsonObject>();
        long cursor = _cursor;
        foreach (var line in lines)
        {
            cursor = line.EndOffset;
            if (line.Text.Trim().Length == 0)
                continue;
            var payloads = OutboxPayload.BuildAll(line.Text);
            if (payloads.Count == 0)
            {
                _notes.Enqueue(LogRedaction.DroppedOutboxLine(line.Text));
                continue;
            }

            foreach (var payload in payloads)
            {
                var text = Json.Str(payload, "text");
                if (text is null)
                {
                    _notes.Enqueue("outbox envelope has no chat text; not sent on the v2 queue");
                    continue;
                }

                var id = NewId();
                ops.Add(new JsonObject
                {
                    ["op"] = "enqueue",
                    ["client_msg_id"] = id,
                    ["text"] = text,
                    ["sourceEnd"] = line.EndOffset
                });
            }
        }

        ops.Add(CursorOp(cursor));
        AppendOps(_outboundPath, ops);
        foreach (var op in ops)
            ApplyOut(op);
    }

    private OutItem? FirstOpen()
    {
        foreach (var id in _outOrder)
        {
            var item = _out[id];
            if (item.State is "queued" or "sent")
                return item;
        }

        return null;
    }

    private void ApplyOut(JsonObject op)
    {
        switch (Json.Str(op, "op"))
        {
            case "cursor":
                _cursor = JsonNum.Long(op, "sourceOffset") ?? _cursor;
                _cursorSet = true;
                break;
            case "enqueue":
                if (Json.Str(op, "client_msg_id") is not { } id)
                    return;
                _out[id] = new OutItem(id, Json.Str(op, "text") ?? "", "queued");
                _outOrder.Add(id);
                break;
            case "sent":
                if (Json.Str(op, "client_msg_id") is { } sent && _out.TryGetValue(sent, out var sending))
                    sending.State = "sent";
                break;
            case "accepted":
                if (Json.Str(op, "client_msg_id") is { } done && _out.TryGetValue(done, out var accepted))
                    accepted.State = "accepted";
                break;
            case "resolve":
                if (Json.Str(op, "client_msg_id") is not { } resolved || !_out.TryGetValue(resolved, out var item))
                    return;
                if (item.State != "sent")
                    return;
                item.State = Json.Str(op, "decision") == "requeue" ? "queued" : "dropped";
                break;
        }
    }

    private void ApplyIn(JsonObject op)
    {
        var id = Json.Str(op, "deliveryId");
        if (string.IsNullOrEmpty(id))
            return;
        var generation = JsonNum.Long(op, "leaseGeneration") ?? 0;
        if (!_in.TryGetValue(id, out var item))
        {
            item = new InItem(id);
            _in[id] = item;
            _inOrder.Add(id);
        }

        switch (Json.Str(op, "op"))
        {
            case "seen":
                if (item.State == "ack_pending" && generation != item.Generation)
                {
                    item.QueuedGeneration = generation;
                    break;
                }

                item.Generation = generation;
                item.State = "seen";
                item.QueuedGeneration = null;
                break;
            case "ack_pending":
                item.Generation = generation;
                item.State = "ack_pending";
                break;
            case "processed":
                item.Generation = generation;
                item.State = "processed";
                break;
            case "fence":
                if (item.Generation == generation && item.State != "processed")
                    item.State = "seen";
                break;
            case "dead":
                item.Generation = generation;
                item.State = "dead";
                break;
        }
    }

    /// <summary>
    /// Offset just past the last newline, or 0 when the file is missing, empty, or one
    /// unfinished line. Complete lines before that offset are left as history.
    /// </summary>
    private static long CompleteLineEnd(string path)
    {
        if (!File.Exists(path))
            return 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = fs.Length;
        if (length == 0)
            return 0;

        var pos = length;
        var buf = new byte[8192];
        while (pos > 0)
        {
            var take = (int)Math.Min(buf.Length, pos);
            pos -= take;
            fs.Seek(pos, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, take);
            for (var i = take - 1; i >= 0; i--)
            {
                if (buf[i] == (byte)'\n')
                    return pos + i + 1;
            }
        }

        return 0;
    }

    private static JsonObject CursorOp(long offset) => new()
    {
        ["op"] = "cursor",
        ["sourceOffset"] = offset
    };

    private static string NewId() => Guid.NewGuid().ToString("D");

    private static void Load(string path, Action<JsonObject> apply)
    {
        if (!File.Exists(path))
            return;
        var text = File.ReadAllText(path);
        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            var cut = text.LastIndexOf('\n');
            text = cut < 0 ? "" : text[..(cut + 1)];
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject op)
                    apply(op);
            }
            catch (System.Text.Json.JsonException)
            {
                // Skip a corrupt line. The next op is still its own record.
            }
        }
    }

    private static void AppendOps(string path, IReadOnlyList<JsonObject> ops)
    {
        if (ops.Count == 0)
            return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        foreach (var op in ops)
        {
            var bytes = Encoding.UTF8.GetBytes(op.ToJsonString(JsonUtil.Opts) + "\n");
            fs.Write(bytes);
        }

        fs.Flush(flushToDisk: true);
    }

    private sealed class OutItem(string id, string text, string state)
    {
        public string Id { get; } = id;
        public string Text { get; } = text;
        public string State { get; set; } = state;
    }

    private sealed class InItem(string deliveryId)
    {
        public string DeliveryId { get; } = deliveryId;
        public long Generation { get; set; }
        public long? QueuedGeneration { get; set; }
        public string State { get; set; } = "seen";
    }
}

internal static class JsonNum
{
    public static long? Long(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
            return null;
        return FromValue(value);
    }

    public static bool? Bool(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
            return null;
        if (value.TryGetValue<bool>(out var b))
            return b;
        return null;
    }

    public static long? FromValue(JsonValue value)
    {
        if (value.TryGetValue<int>(out var i))
            return i;
        if (value.TryGetValue<long>(out var l))
            return l;
        return null;
    }
}
