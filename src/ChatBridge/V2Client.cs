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
internal readonly record struct V2ChatStep(bool Hold, string? ClientMsgId, JsonObject? Frame, bool ResetSession = false)
{
    public static V2ChatStep Idle { get; } = new(false, null, null);

    /// <summary>
    /// A <c>drop</c> invalidated the in-flight send. The deployed <c>accepted</c> frame has no
    /// client id, so this session must reconnect before another chat is sent.
    /// </summary>
    public static V2ChatStep Fenced { get; } = new(false, null, null, true);

    public bool Send => Frame is not null && !ResetSession;
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
/// Inbound: a delivery is fsynced as <c>seen</c> with its payload, then handed to
/// <see cref="Consumers"/>, then fsynced as <c>handed_off</c> and <c>ack_pending</c>
/// before the ack frame is returned. A failed handoff stays <c>seen</c> and is not acked.
/// Completion is <c>ack_result</c> <c>processed</c> or <c>idempotent</c> for that
/// delivery and lease generation. A seen or fenced row is not complete and may be re-leased.
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

    private string? _outboxPath;
    private long _outboundApplied;
    private long _cursor;
    private bool _cursorSet;
    private bool _inboxAuth;
    private bool _initialPullSent;
    private bool _pullAgain;
    private int _pulls;
    private string? _ackInFlight;
    // True after a sent row is dropped, until the next connection. A late accepted on this
    // socket must not complete a different chat: the frame has no client_msg_id.
    private bool _acceptFence;

    private V2Client(string baseDir)
    {
        _outboundPath = Path.Combine(baseDir, OutboundName);
        _inboundPath = Path.Combine(baseDir, InboundName);
        Load(_outboundPath, ApplyOut);
        Load(_inboundPath, ApplyIn);
        _outboundApplied = CompleteLineEnd(_outboundPath);
    }

    public static V2Client Open(string baseDir) => new(baseDir);

    /// <summary>
    /// Durable wake queue. Null fails closed: a delivery is stored as <c>seen</c> and not acked.
    /// The bridge sets this to the room-inbox and <c>agents/&lt;id&gt;/inbox.jsonl</c> handoff.
    /// </summary>
    public IV2ConsumerQueue? Consumers { get; set; }

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
            _inboxAuth = false;
            _initialPullSent = false;
            _pullAgain = false;
            _pulls = 0;
            _ackInFlight = null;
            CatchUpOutbound();
            // New socket. An accepted for a chat dropped on the previous connection cannot arrive here.
            if (_acceptFence)
            {
                AppendOutbound(new[]
                {
                    new JsonObject { ["op"] = "accept_open" }
                });
            }

            if (_cursorSet)
                return;
            // Snapshot after the last complete line. A tail with no newline is still
            // being written; starting at raw EOF would skip its head and never send it.
            var length = CompleteLineEnd(outboxPath);
            AppendOutbound(new[] { CursorOp(length) });
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
        {
            CatchUpOutbound();
            ImportLocked();
        }
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
            AppendOutbound(new[] { op });
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
            CatchUpOutbound();
            ImportLocked();
            if (_acceptFence)
            {
                _notes.Enqueue("dropped send fenced this session; reconnect before the next chat");
                return V2ChatStep.Fenced;
            }

            var head = FirstOpen();
            if (head is null)
                return V2ChatStep.Idle;
            if (head.State == "sent")
                return new V2ChatStep(true, head.Id, null);
            if (head.State != "queued")
                return V2ChatStep.Idle;

            var sent = new JsonObject { ["op"] = "sent", ["client_msg_id"] = head.Id };
            AppendOutbound(new[] { sent });
            return new V2ChatStep(false, head.Id, ChatFrame(head.Text));
        }
    }

    /// <summary>
    /// Resolve one uncertain <c>sent</c> row. <paramref name="decision"/> is <c>requeue</c>
    /// (may duplicate on the server) or <c>drop</c> (do not send). Only a <c>sent</c> row can
    /// be resolved. This does not send by itself. The operator command is
    /// <c>reconcile --id &lt;client_msg_id&gt; requeue|drop</c>. A <c>sent</c> row is never resent
    /// until that explicit decision.
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
            AppendOutbound(new[] { op });
            return true;
        }
    }

    /// <summary>Ids of <c>sent</c> rows still waiting on <c>accepted</c>. Empty when nothing is held.</summary>
    public IReadOnlyList<string> UncertainIds()
    {
        lock (_gate)
        {
            CatchUpOutbound();
            var ids = new List<string>();
            foreach (var id in _outOrder)
            {
                if (_out[id].State == "sent")
                    ids.Add(id);
            }

            return ids;
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
        if (TryArmAck() is { } ack)
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

        if (_in.TryGetValue(id, out var existing) && existing.Generation == generation)
        {
            if (existing.State is "processed" or "dead" or "ack_pending")
                return V2InboundResult.None;
            // Handoff already landed. Replay the ack; do not enqueue again.
            if (existing.State == "handed_off")
                return ArmAck();
        }

        var payload = V2Payload.Copy(frame);
        var seen = new JsonObject
        {
            ["op"] = "seen",
            ["deliveryId"] = id,
            ["leaseGeneration"] = generation.Value,
            ["payload"] = payload
        };
        AppendInbound(seen);
        // A newer lease that arrives while this id's ack is still in flight is stored
        // on the row and handed off only after that ack settles. Overwriting now would
        // let the in-flight result complete the wrong generation.
        if (!_in.TryGetValue(id, out var stored))
            return V2InboundResult.None;
        if (stored.State is "ack_pending" or "handed_off" && stored.Generation != generation)
            return V2InboundResult.None;
        if (stored.State == "seen" && !CommitHandoff(stored))
            return V2InboundResult.None;
        return ArmAck();
    }

    private V2InboundResult OnAckResult(JsonObject frame)
    {
        var state = Json.Str(frame, "state");
        var idempotent = JsonNum.Bool(frame, "idempotent") == true;
        if (state == "processed" || idempotent)
        {
            if (!TryCorrelate(frame, out var item, out var note) || item is null)
            {
                if (note is not null)
                    _notes.Enqueue(note);
                return V2InboundResult.None;
            }

            if (note is not null)
                _notes.Enqueue(note);
            if (item.State is not ("processed" or "dead"))
                Complete(item, idempotent ? "idempotent" : "processed");
            return ContinueAfterAck();
        }

        if (state is "lease_fenced" or "lease_expired")
        {
            if (!TryCorrelate(frame, out var item, out var note) || item is null)
            {
                if (note is not null)
                    _notes.Enqueue(note);
                return V2InboundResult.None;
            }

            if (note is not null)
                _notes.Enqueue(note);
            if (item.State is not ("processed" or "dead"))
                Fence(item, state);
            return ContinueAfterAck();
        }

        _notes.Enqueue("ack_result did not confirm completion");
        return V2InboundResult.None;
    }

    private V2InboundResult OnError(JsonObject frame)
    {
        var code = Json.Str(frame, "code");
        if (code is "lease_fenced" or "lease_expired")
        {
            if (!TryCorrelate(frame, out var item, out var note) || item is null)
            {
                if (note is not null)
                    _notes.Enqueue(note);
                return V2InboundResult.None;
            }

            if (note is not null)
                _notes.Enqueue(note);
            if (item.State is not ("processed" or "dead"))
                Fence(item, code);
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
        // A reconcile drop may be on disk already. See it before FirstOpen, or the late
        // accepted completes whichever row the pump sent next.
        CatchUpOutbound();
        if (_acceptFence)
        {
            _notes.Enqueue("accepted ignored; dropped send fenced this session");
            return V2InboundResult.None;
        }

        var named = Json.Str(frame, "client_msg_id");
        var head = FirstOpen();
        if (head is not { State: "sent" })
        {
            _notes.Enqueue("accepted with no in-flight send; not applied");
            return V2InboundResult.None;
        }

        if (named is not null && !string.Equals(named, head.Id, StringComparison.Ordinal))
        {
            _notes.Enqueue("accepted names a different client_msg_id; not applied");
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
        AppendOutbound(new[] { op });
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
        AppendInbound(op);
        if (_ackInFlight == id)
            _ackInFlight = null;
        return V2InboundResult.None;
    }

    private V2InboundResult ContinueAfterAck()
    {
        var send = new List<JsonObject>();
        if (TryArmAck() is { } ack)
            send.Add(ack);
        else if ((_pullAgain || (_inboxAuth && !_initialPullSent)) && ArmPull() is { } pull)
            send.Add(pull);
        return new V2InboundResult(send, true, null);
    }

    private V2InboundResult ArmAck()
    {
        var send = new List<JsonObject>();
        if (TryArmAck() is { } ack)
            send.Add(ack);
        return new V2InboundResult(send, false, null);
    }

    /// <summary>
    /// Ack a row whose consumer handoff is already durable. A <c>seen</c> row is not acked:
    /// that state means the handoff has not succeeded, and a later lease can retry it.
    /// </summary>
    private JsonObject? TryArmAck()
    {
        if (_ackInFlight is not null)
            return null;

        foreach (var id in _inOrder)
        {
            var item = _in[id];
            if (item.State is "ack_pending" or "handed_off")
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
        AppendInbound(op);
        _ackInFlight = item.DeliveryId;
        return AckFrame(item.DeliveryId, item.Generation);
    }

    /// <summary>
    /// Fsync the consumer handoff, then <c>handed_off</c>. False leaves the row <c>seen</c>
    /// so the server can re-lease it. The ack frame is not returned in that case.
    /// </summary>
    private bool CommitHandoff(InItem item)
    {
        if (V2Payload.TryQueue(item.DeliveryId, item.Generation, item.Payload) is not { } delivery)
        {
            _notes.Enqueue("delivery has no text payload; not acked");
            return false;
        }

        if (Consumers is null)
        {
            _notes.Enqueue("no consumer queue; delivery not acked");
            return false;
        }

        try
        {
            if (!Consumers.TryEnqueue(delivery, out var error))
            {
                _notes.Enqueue(string.IsNullOrEmpty(error)
                    ? "consumer enqueue failed; delivery not acked"
                    : "consumer enqueue failed; delivery not acked (" + error + ")");
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _notes.Enqueue("consumer enqueue failed; delivery not acked (" + ex.GetType().Name + ")");
            return false;
        }

        AppendInbound(new JsonObject
        {
            ["op"] = "handed_off",
            ["deliveryId"] = item.DeliveryId,
            ["leaseGeneration"] = item.Generation
        });
        return item.State == "handed_off";
    }

    private void Complete(InItem item, string how)
    {
        if (_ackInFlight == item.DeliveryId)
            _ackInFlight = null;
        if (item.State is "processed" or "dead")
            return;
        AppendInbound(new JsonObject
        {
            ["op"] = "processed",
            ["deliveryId"] = item.DeliveryId,
            ["leaseGeneration"] = item.Generation,
            ["how"] = how
        });
        PromoteQueuedLease(item);
    }

    private void Fence(InItem item, string code)
    {
        if (_ackInFlight == item.DeliveryId)
            _ackInFlight = null;
        if (item.State is "processed" or "dead")
            return;
        AppendInbound(new JsonObject
        {
            ["op"] = "fence",
            ["deliveryId"] = item.DeliveryId,
            ["leaseGeneration"] = item.Generation,
            ["code"] = code
        });
        // Seen again, not handed off. The same generation is not acked until a new delivery
        // commits the consumer queue. A generation that arrived during this ack is promoted below.
        PromoteQueuedLease(item);
    }

    /// <summary>
    /// Match <c>ack_result</c> / lease errors to one row. A frame that names
    /// <c>deliveryId</c> settles only that row, and <c>leaseGeneration</c> must match
    /// when it is present. A frame with neither id is applied only when one ack is in
    /// flight — the §11 fixtures omit the id. A mismatch does not complete a different row.
    /// </summary>
    private bool TryCorrelate(JsonObject frame, out InItem? item, out string? note)
    {
        item = null;
        note = null;
        var id = Json.Str(frame, "deliveryId");
        var generation = JsonNum.Long(frame, "leaseGeneration");
        if (!string.IsNullOrEmpty(id))
        {
            if (!_in.TryGetValue(id, out var named))
            {
                note = "ack_result deliveryId does not match a local delivery; not applied";
                return false;
            }

            if (generation is not null && generation.Value != named.Generation)
            {
                note = "ack_result leaseGeneration does not match that delivery; not applied";
                return false;
            }

            if (named.State is "processed" or "dead")
            {
                note = "ack_result for a finished delivery; ignored";
                if (_ackInFlight == id)
                    _ackInFlight = null;
                item = named;
                return true;
            }

            if (named.State is not ("ack_pending" or "handed_off"))
            {
                note = "ack_result for a delivery that is not waiting on ack; not applied";
                return false;
            }

            if (_ackInFlight == id)
                _ackInFlight = null;
            item = named;
            return true;
        }

        var pending = new List<InItem>();
        foreach (var key in _inOrder)
        {
            if (_in[key].State == "ack_pending")
                pending.Add(_in[key]);
        }

        InItem? inflight = null;
        if (_ackInFlight is not null && _in.TryGetValue(_ackInFlight, out var marked) && marked.State == "ack_pending")
            inflight = marked;
        else if (pending.Count == 1)
            inflight = pending[0];

        if (inflight is null)
        {
            note = pending.Count > 1
                ? "ack_result has no deliveryId and more than one ack is pending; not applied"
                : "ack_result has no deliveryId and no in-flight ack; not applied";
            return false;
        }

        if (generation is not null && generation.Value != inflight.Generation)
        {
            note = "ack_result leaseGeneration does not match the in-flight ack; not applied";
            return false;
        }

        _ackInFlight = null;
        item = inflight;
        note = null;
        return true;
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
        if (item.QueuedPayload is not null)
            seen["payload"] = item.QueuedPayload.DeepClone();
        AppendInbound(seen);
        item.QueuedPayload = null;
        if (item.State == "seen")
            CommitHandoff(item);
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
        AppendOutbound(ops);
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
                // Shared by OnAccepted, Load, and AppendOutbound/CatchUp. A drop fences this
                // connection, so a late accepted must not complete the dropped row or a later one.
                if (_acceptFence)
                    return;
                if (Json.Str(op, "client_msg_id") is not { } done || !_out.TryGetValue(done, out var acceptedRow))
                    return;
                if (acceptedRow.State != "sent")
                    return;
                acceptedRow.State = "accepted";
                break;
            case "resolve":
                if (Json.Str(op, "client_msg_id") is not { } resolved || !_out.TryGetValue(resolved, out var item))
                    return;
                if (item.State != "sent")
                    return;
                if (Json.Str(op, "decision") == "requeue")
                {
                    item.State = "queued";
                    return;
                }

                item.State = "dropped";
                // Tombstone. Later accepted frames on this connection do not complete another row.
                _acceptFence = true;
                break;
            case "accept_open":
                _acceptFence = false;
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
                if (item.State is "ack_pending" or "handed_off" && generation != item.Generation)
                {
                    item.QueuedGeneration = generation;
                    if (op["payload"] is JsonObject queued)
                        item.QueuedPayload = (JsonObject)queued.DeepClone();
                    break;
                }

                item.Generation = generation;
                item.State = "seen";
                item.QueuedGeneration = null;
                item.QueuedPayload = null;
                if (op["payload"] is JsonObject payload)
                    item.Payload = (JsonObject)payload.DeepClone();
                break;
            case "handed_off":
                if (item.State is "processed" or "dead" or "ack_pending")
                    break;
                item.Generation = generation;
                item.State = "handed_off";
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
        // Replay goes through ApplyOut. A drop earlier in the file fences later accepted lines.
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
        public JsonObject? Payload { get; set; }
        public JsonObject? QueuedPayload { get; set; }
        public string State { get; set; } = "seen";
    }

    private void AppendInbound(JsonObject op)
    {
        AppendOps(_inboundPath, new[] { op });
        ApplyIn(op);
    }

    /// <summary>
    /// Apply resolve lines an operator appended while this process was idle.
    /// Bytes this process already applied are not read again.
    /// </summary>
    private void CatchUpOutbound()
    {
        var end = CompleteLineEnd(_outboundPath);
        if (end <= _outboundApplied)
            return;
        byte[] buf;
        using (var fs = new FileStream(_outboundPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(_outboundApplied, SeekOrigin.Begin);
            var len = (int)(end - _outboundApplied);
            buf = new byte[len];
            fs.ReadExactly(buf, 0, len);
        }

        _outboundApplied = end;
        var text = Encoding.UTF8.GetString(buf);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject op)
                    ApplyOut(op);
            }
            catch (System.Text.Json.JsonException)
            {
                // A corrupt operator line does not resend anything.
            }
        }
    }

    private void AppendOutbound(IReadOnlyList<JsonObject> ops)
    {
        CatchUpOutbound();
        if (_acceptFence)
        {
            // Do not persist an accepted line chosen by FirstOpen after a live drop.
            var kept = new List<JsonObject>(ops.Count);
            foreach (var op in ops)
            {
                if (Json.Str(op, "op") == "accepted")
                    continue;
                kept.Add(op);
            }

            ops = kept;
        }

        AppendOps(_outboundPath, ops);
        _outboundApplied = CompleteLineEnd(_outboundPath);
        foreach (var op in ops)
            ApplyOut(op);
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
