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
internal readonly record struct V2ChatStep(
    bool Hold,
    string? ClientMsgId,
    JsonObject? Frame,
    bool ResetSession = false,
    TimeSpan? Wait = null,
    bool RateLimitWait = false)
{
    public static V2ChatStep Idle { get; } = new(false, null, null);

    /// <summary>
    /// A <c>drop</c> or an opt-in echo release invalidated correlation on this socket.
    /// The deployed <c>accepted</c> frame has no client id, so this session must reconnect
    /// before another chat is sent.
    /// </summary>
    public static V2ChatStep Fenced { get; } = new(false, null, null, true);

    public bool Send => Frame is not null && !ResetSession;
}

/// <summary>
/// Opt-in knobs for the v2 outbound pump. Both switches default off.
/// Neither switch makes delivery exactly-once.
/// </summary>
internal sealed class V2OutboundOptions
{
    /// <summary>
    /// When true, outbound chat frames include <c>client_msg_id</c> and a rate-limit retry
    /// reuses that same id. §11 has no server idempotency key, so this does not by itself
    /// stop a duplicate if a retry follows a lost <c>accepted</c>.
    /// </summary>
    public bool SendDedup { get; init; }

    /// <summary>
    /// Compatibility path for a relay that echoes the chat and never sends <c>accepted</c>.
    /// Default false. An own-nick exact-text echo is never <c>accepted</c>.
    /// When true, one unambiguous in-flight echo from this session is parked as
    /// <c>echo_observed</c> and the socket is fenced. Replay, identical text on another
    /// open row, and a row this session did not send do not release the head.
    /// </summary>
    public bool EchoCompat { get; init; }

    public string? OwnNick { get; init; }

    /// <summary>Send attempts, including the first, before a rate-limited row becomes <c>uncertain</c>.</summary>
    public int MaxRateLimitAttempts { get; init; } = 5;

    public TimeSpan DefaultRateLimitDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on a server retry delay so a huge value cannot pin the head.</summary>
    public TimeSpan MaxRateLimitDelay { get; init; } = TimeSpan.FromSeconds(30);

    public static V2OutboundOptions From(RelayConfig cfg) => new()
    {
        SendDedup = cfg.V2SendDedup,
        EchoCompat = cfg.V2EchoCompat,
        OwnNick = cfg.Nick
    };
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
/// Outbound states stay separate. <c>sent</c> is fsynced before SendAsync. It records
/// the intent to write, not a proven socket write. A crash between that record and the
/// send leaves the row <c>sent</c> without proof the bytes left. It is not server
/// acceptance. <c>accepted</c> is only a correlated accept receipt. <c>echo_observed</c>
/// is own-nick exact text and is never written as <c>accepted</c>. <c>rate_limited</c>
/// keeps the row and retries after the server delay, up to a bound, reusing
/// <c>client_msg_id</c>, and only when this connection actually submitted that chat.
/// <c>uncertain</c> is a lost or exhausted outcome: it is not dropped and not requeued.
/// A <c>sent</c> or <c>uncertain</c> head holds the pump. A missed <c>accepted</c> is
/// not resent, because §11 says that retry can store a second message. This is not
/// exactly-once delivery. The local id is <c>client_msg_id</c>. It is omitted from the
/// chat frame unless <see cref="V2OutboundOptions.SendDedup"/> is on. A live <c>drop</c>
/// fences every later <c>accepted</c> on this connection. An opt-in echo release fences
/// unnamed receipts and receipts for any other id; a receipt that names the
/// <c>echo_observed</c> row can still complete that row. The next chat waits for a new
/// socket either way. <c>requeue</c> does not fence.
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
    private readonly V2OutboundOptions _options;
    private enum OutboundFence
    {
        None = 0,
        Drop,
        Echo
    }

    private readonly Func<DateTimeOffset> _clock;
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
    // Set after a sent row is dropped or an opt-in echo release, until the next connection.
    // Drop rejects every accepted. Echo rejects unnamed receipts and any other id, and
    // still allows a receipt that names the echo_observed row. The deployed frame has no
    // client_msg_id, so an ambiguous accepted must not complete a different chat.
    private OutboundFence _fence;
    private string? _fenceNote;
    // The row this connection handed to the socket. Echo compat will not release any other row.
    private string? _sessionOutstanding;

    private V2Client(string baseDir, V2OutboundOptions options, Func<DateTimeOffset> clock)
    {
        _options = options;
        _clock = clock;
        _outboundPath = Path.Combine(baseDir, OutboundName);
        _inboundPath = Path.Combine(baseDir, InboundName);
        Load(_outboundPath, ApplyOut);
        Load(_inboundPath, ApplyIn);
        _outboundApplied = CompleteLineEnd(_outboundPath);
    }

    public static V2Client Open(string baseDir, V2OutboundOptions? options = null, Func<DateTimeOffset>? clock = null)
        => new(baseDir, options ?? new V2OutboundOptions(), clock ?? (() => DateTimeOffset.UtcNow));

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
            _sessionOutstanding = null;
            CatchUpOutbound();
            // New socket. An accepted for a chat dropped or echo-released on the previous
            // connection cannot arrive here.
            if (_fence != OutboundFence.None)
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
    /// Returns the next chat, a rate-limit wait, or a hold when the head was handed to the
    /// socket and no correlated <c>accepted</c> has been observed. The <c>sent</c> row is
    /// fsynced before the frame is returned. That record is the intent to write, not a
    /// proven socket write, and it is not server acceptance.
    /// </summary>
    public V2ChatStep NextChat()
    {
        lock (_gate)
        {
            CatchUpOutbound();
            ImportLocked();
            if (_fence != OutboundFence.None)
            {
                _notes.Enqueue(_fenceNote ?? "dropped send fenced this session; reconnect before the next chat");
                return V2ChatStep.Fenced;
            }

            var head = FirstOpen();
            if (head is null)
                return V2ChatStep.Idle;
            if (head.State is "sent" or "uncertain")
                return new V2ChatStep(true, head.Id, null);
            if (head.State == "rate_limited")
            {
                var now = _clock();
                var dueMs = head.NotBeforeMs ?? now.ToUnixTimeMilliseconds();
                var due = DateTimeOffset.FromUnixTimeMilliseconds(dueMs);
                if (now < due)
                    return new V2ChatStep(true, head.Id, null, false, due - now, true);
                if (head.Attempt >= Math.Max(1, _options.MaxRateLimitAttempts))
                {
                    AppendOutbound(new[] { UncertainOp(head.Id, "rate_limit_exhausted") });
                    return new V2ChatStep(true, head.Id, null);
                }

                var attempt = head.Attempt + 1;
                AppendOutbound(new[] { SentOp(head.Id, attempt) });
                _sessionOutstanding = head.Id;
                return new V2ChatStep(false, head.Id, OutboundFrame(head.Text, head.Id));
            }

            if (head.State != "queued")
                return V2ChatStep.Idle;

            AppendOutbound(new[] { SentOp(head.Id, 1) });
            _sessionOutstanding = head.Id;
            return new V2ChatStep(false, head.Id, OutboundFrame(head.Text, head.Id));
        }
    }

    /// <summary>
    /// Resolve one held row. <paramref name="decision"/> is <c>requeue</c>
    /// (may duplicate on the server) or <c>drop</c> (do not send). A <c>sent</c>,
    /// <c>uncertain</c>, <c>rate_limited</c>, or <c>echo_observed</c> row can be resolved.
    /// <c>accepted</c> cannot. This does not send by itself. The operator command is
    /// <c>reconcile --id &lt;client_msg_id&gt; requeue|drop</c>. Nothing here is automatic.
    /// A <c>drop</c> fences this connection until the next socket. <c>requeue</c> does not.
    /// </summary>
    public bool ResolveUncertain(string clientMsgId, string decision)
    {
        if (decision is not ("requeue" or "drop"))
            return false;
        lock (_gate)
        {
            if (!_out.TryGetValue(clientMsgId, out var item) || item.State is not ("sent" or "uncertain" or "rate_limited" or "echo_observed"))
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

    /// <summary>
    /// Ids of <c>sent</c> and explicit <c>uncertain</c> rows still waiting on a correlated
    /// receipt. Empty when nothing is held. <c>rate_limited</c> and <c>echo_observed</c> are
    /// reported separately; neither is <c>accepted</c>.
    /// </summary>
    public IReadOnlyList<string> UncertainIds()
    {
        lock (_gate)
        {
            CatchUpOutbound();
            return IdsWhere(item => item.State is "sent" or "uncertain");
        }
    }

    public IReadOnlyList<string> RateLimitedIds()
    {
        lock (_gate)
        {
            CatchUpOutbound();
            return IdsWhere(item => item.State == "rate_limited");
        }
    }

    public IReadOnlyList<string> EchoObservedIds()
    {
        lock (_gate)
        {
            CatchUpOutbound();
            return IdsWhere(item => item.State == "echo_observed");
        }
    }

    /// <summary>Ledger state of one outbound row, or null when the id is unknown.</summary>
    internal string? DeliveryState(string clientMsgId)
    {
        lock (_gate)
        {
            CatchUpOutbound();
            return _out.TryGetValue(clientMsgId, out var item) ? item.State : null;
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
                case "warn" when Json.Str(frame, "code") == "rate_limited":
                case "rate_limited":
                    return OnRateLimited(frame);
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

    /// <summary>Deployed chat frame. No <c>client_msg_id</c> unless send-dedup is explicitly on.</summary>
    public static JsonObject ChatFrame(string text) => new()
    {
        ["v"] = 2,
        ["type"] = "chat",
        ["text"] = text
    };

    private JsonObject OutboundFrame(string text, string clientMsgId)
    {
        var frame = ChatFrame(text);
        if (_options.SendDedup)
            frame["client_msg_id"] = clientMsgId;
        return frame;
    }

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

        if (code == "rate_limited")
            return OnRateLimited(frame);

        return V2InboundResult.None;
    }

    private V2InboundResult OnAccepted(JsonObject frame)
    {
        // A reconcile drop may be on disk already. See it before choosing a row, or the late
        // accepted completes whichever row the pump sent next.
        CatchUpOutbound();
        var named = Json.Str(frame, "client_msg_id");
        if (AcceptedBlockedByFence(named))
        {
            _notes.Enqueue(_fence == OutboundFence.Echo
                ? "accepted ignored; echo_observed fenced this session"
                : "accepted ignored; dropped send fenced this session");
            return V2InboundResult.None;
        }

        OutItem? target;
        if (named is not null)
        {
            if (!_out.TryGetValue(named, out target) || target.State is not ("sent" or "uncertain" or "rate_limited" or "echo_observed"))
            {
                _notes.Enqueue("accepted names a different client_msg_id; not applied");
                return V2InboundResult.None;
            }
        }
        else
        {
            target = UnnamedAcceptTarget();
            if (target is null)
            {
                _notes.Enqueue(RowsIn("sent").Count > 1
                    ? "accepted has no client_msg_id and more than one send is in flight; not applied"
                    : "accepted with no in-flight send; not applied");
                return V2InboundResult.None;
            }
        }

        var op = new JsonObject
        {
            ["op"] = "accepted",
            ["client_msg_id"] = target.Id
        };
        if (Json.Str(frame, "messageId") is { } messageId)
            op["messageId"] = messageId;
        if (Json.Str(frame, "ingressId") is { } ingressId)
            op["ingressId"] = ingressId;
        AppendOutbound(new[] { op });
        return new V2InboundResult(Array.Empty<JsonObject>(), true, null);
    }

    /// <summary>
    /// The server rejected a chat this connection actually submitted. Keep that row and
    /// schedule a bounded retry. A <c>rate_limited</c> JOIN, or any other control error,
    /// is not that rejection: it does not move a persisted <c>sent</c> row, including a
    /// lost receipt that survived reconnect. A named id applies only when it is the
    /// current-session row and that row is still <c>sent</c>. An unnamed frame applies
    /// only when that same row is the single in-flight <c>sent</c> row.
    /// </summary>
    private V2InboundResult OnRateLimited(JsonObject frame)
    {
        CatchUpOutbound();
        if (_sessionOutstanding is null
            || !_out.TryGetValue(_sessionOutstanding, out var submitted)
            || submitted.State != "sent")
        {
            _notes.Enqueue("rate_limited did not match a chat submitted this session; not applied");
            return V2InboundResult.None;
        }

        var named = Json.Str(frame, "client_msg_id");
        OutItem target;
        if (named is not null)
        {
            if (!string.Equals(named, submitted.Id, StringComparison.Ordinal))
            {
                _notes.Enqueue("rate_limited names a different client_msg_id; not applied");
                return V2InboundResult.None;
            }

            target = submitted;
        }
        else
        {
            var inflight = RowsIn("sent");
            if (inflight.Count != 1 || !string.Equals(inflight[0].Id, submitted.Id, StringComparison.Ordinal))
            {
                _notes.Enqueue(inflight.Count > 1
                    ? "rate_limited has no client_msg_id and more than one send is in flight; not applied"
                    : "rate_limited did not match a chat submitted this session; not applied");
                return V2InboundResult.None;
            }

            target = submitted;
        }

        if (target.Attempt >= Math.Max(1, _options.MaxRateLimitAttempts))
        {
            AppendOutbound(new[] { UncertainOp(target.Id, "rate_limit_exhausted") });
            _notes.Enqueue("rate_limited retry budget exhausted; row is uncertain and was not dropped or requeued");
            return new V2InboundResult(Array.Empty<JsonObject>(), true, null);
        }

        var delay = RateLimitDelay(frame);
        var notBefore = _clock().Add(delay).ToUnixTimeMilliseconds();
        AppendOutbound(new[]
        {
            new JsonObject
            {
                ["op"] = "rate_limited",
                ["client_msg_id"] = target.Id,
                ["attempt"] = target.Attempt,
                ["not_before_ms"] = notBefore
            }
        });
        _notes.Enqueue("rate_limited; row kept for a bounded retry");
        return new V2InboundResult(Array.Empty<JsonObject>(), true, null);
    }

    /// <summary>
    /// Room echo evidence. Never writes <c>accepted</c>.
    /// A welcome replay is ignored. Identical text across more than one open row is ignored.
    /// An echo that does not match the single in-flight <c>sent</c> row is ignored.
    /// With echo compat off, a matching echo is recorded and the row stays <c>sent</c> (the pump holds).
    /// With echo compat on, that match also has to be the row this session handed to the socket;
    /// the row becomes <c>echo_observed</c> and the socket is fenced. Returns true when the
    /// pump should wake because the head was released.
    /// </summary>
    internal bool ObserveEcho(string? nick, string? text, bool replay)
    {
        lock (_gate)
        {
            if (replay)
                return false;

            if (string.IsNullOrEmpty(_options.OwnNick)
                || !string.Equals(nick, _options.OwnNick, StringComparison.Ordinal)
                || text is null)
                return false;

            var open = OpenRows().Where(row => string.Equals(row.Text, text, StringComparison.Ordinal)).ToList();
            var sent = open.Where(row => row.State == "sent").ToList();
            if (sent.Count != 1 || open.Count != 1)
            {
                if (open.Count > 1)
                    _notes.Enqueue("echo matches more than one open row; not applied");
                else if (sent.Count == 0 && open.Count == 1)
                    _notes.Enqueue("echo does not match the in-flight send; not applied");
                return false;
            }

            var row = sent[0];
            var release = _options.EchoCompat
                && string.Equals(row.Id, _sessionOutstanding, StringComparison.Ordinal);
            if (row.Echo && !release)
                return false;

            AppendOutbound(new[]
            {
                new JsonObject
                {
                    ["op"] = "echo",
                    ["client_msg_id"] = row.Id,
                    ["release"] = release
                }
            });
            _notes.Enqueue(release
                ? "echo_observed for one in-flight send; not accepted"
                : "echo_observed; not accepted");
            return release;
        }
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
        // An in-flight or held row wins over an earlier row that was requeued. Otherwise
        // echo-release A, send B, then requeue A would select A while B is still sent.
        OutItem? queued = null;
        foreach (var id in _outOrder)
        {
            var item = _out[id];
            if (item.State is "sent" or "rate_limited" or "uncertain")
                return item;
            // echo_observed is evidence, not acceptance, and it does not block the rows behind it.
            // accepted and dropped are finished.
            if (queued is null && item.State == "queued")
                queued = item;
        }

        return queued;
    }

    private List<OutItem> RowsIn(string state)
    {
        var rows = new List<OutItem>();
        foreach (var id in _outOrder)
        {
            if (_out[id].State == state)
                rows.Add(_out[id]);
        }

        return rows;
    }

    private List<OutItem> OpenRows()
    {
        var rows = new List<OutItem>();
        foreach (var id in _outOrder)
        {
            if (_out[id].State is "queued" or "sent" or "rate_limited" or "uncertain" or "echo_observed")
                rows.Add(_out[id]);
        }

        return rows;
    }

    /// <summary>
    /// Unnamed <c>accepted</c> completes the single in-flight <c>sent</c> row.
    /// With none in flight, it completes a single <c>uncertain</c> row. Anything else is ambiguous
    /// and is not applied — including a row that is only <c>echo_observed</c>.
    /// </summary>
    private OutItem? UnnamedAcceptTarget()
    {
        var sent = RowsIn("sent");
        if (sent.Count == 1)
            return sent[0];
        if (sent.Count > 1)
            return null;
        var uncertain = RowsIn("uncertain");
        return uncertain.Count == 1 ? uncertain[0] : null;
    }

    private List<string> IdsWhere(Func<OutItem, bool> match)
    {
        var ids = new List<string>();
        foreach (var id in _outOrder)
        {
            if (match(_out[id]))
                ids.Add(id);
        }

        return ids;
    }

    private static JsonObject SentOp(string id, int attempt) => new()
    {
        ["op"] = "sent",
        ["client_msg_id"] = id,
        ["attempt"] = attempt
    };

    private static JsonObject UncertainOp(string id, string reason) => new()
    {
        ["op"] = "uncertain",
        ["client_msg_id"] = id,
        ["reason"] = reason
    };

    private TimeSpan RateLimitDelay(JsonObject frame)
    {
        // Clamp before TimeSpan.FromMilliseconds. A huge retryAfterMs throws, and
        // retryAfter seconds * 1000 overflows long before that conversion.
        var capMs = RateLimitCapMs();
        var rawMs = JsonNum.Long(frame, "retryAfterMs") ?? JsonNum.Long(frame, "retry_after_ms");
        long ms;
        if (rawMs is { } given)
        {
            ms = given;
        }
        else if (JsonNum.Long(frame, "retryAfter") is { } seconds)
        {
            if (seconds <= 0 || capMs / 1000 < seconds)
                ms = seconds <= 0 ? 0 : capMs;
            else
                ms = seconds * 1000;
        }
        else
        {
            return CapDelay(_options.DefaultRateLimitDelay);
        }

        if (ms < 0)
            ms = 0;
        else if (ms > capMs)
            ms = capMs;
        return TimeSpan.FromMilliseconds(ms);
    }

    private long RateLimitCapMs()
    {
        var cap = _options.MaxRateLimitDelay;
        if (cap <= TimeSpan.Zero)
            return 0;
        // TimeSpan.MaxValue.TotalMilliseconds is 922337203685477. Stay inside that.
        var ms = cap.TotalMilliseconds;
        if (ms >= 922337203685477d)
            return 922337203685477;
        return (long)ms;
    }

    private TimeSpan CapDelay(TimeSpan delay)
    {
        var cap = _options.MaxRateLimitDelay;
        if (cap < TimeSpan.Zero)
            cap = TimeSpan.Zero;
        if (delay < TimeSpan.Zero)
            return TimeSpan.Zero;
        return delay > cap ? cap : delay;
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
                {
                    sending.State = "sent";
                    var attempt = JsonNum.Long(op, "attempt");
                    sending.Attempt = attempt is null ? Math.Max(sending.Attempt, 1) : (int)attempt.Value;
                    if (sending.Attempt < 1)
                        sending.Attempt = 1;
                }

                break;
            case "accepted":
                // Shared by OnAccepted, Load, and AppendOutbound/CatchUp. A drop fences every
                // accepted. An echo fence still allows a receipt that names the echo_observed
                // row, and rejects an unnamed receipt or any other id.
                if (Json.Str(op, "client_msg_id") is not { } done || !_out.TryGetValue(done, out var acceptedRow))
                    return;
                if (_fence == OutboundFence.Drop)
                    return;
                if (_fence == OutboundFence.Echo && acceptedRow.State != "echo_observed")
                    return;
                if (acceptedRow.State is not ("sent" or "uncertain" or "rate_limited" or "echo_observed"))
                    return;
                acceptedRow.State = "accepted";
                acceptedRow.NotBeforeMs = null;
                break;
            case "rate_limited":
                if (Json.Str(op, "client_msg_id") is not { } limited || !_out.TryGetValue(limited, out var limitedRow))
                    return;
                if (limitedRow.State != "sent")
                    return;
                limitedRow.State = "rate_limited";
                if (JsonNum.Long(op, "attempt") is { } limitedAttempt)
                    limitedRow.Attempt = (int)limitedAttempt;
                limitedRow.NotBeforeMs = JsonNum.Long(op, "not_before_ms");
                break;
            case "uncertain":
                if (Json.Str(op, "client_msg_id") is not { } unknown || !_out.TryGetValue(unknown, out var unknownRow))
                    return;
                if (unknownRow.State is not ("sent" or "rate_limited"))
                    return;
                unknownRow.State = "uncertain";
                unknownRow.NotBeforeMs = null;
                break;
            case "echo":
                // Evidence only. release:true is the opt-in compat path and still is not accepted.
                if (Json.Str(op, "client_msg_id") is not { } echoed || !_out.TryGetValue(echoed, out var echoedRow))
                    return;
                if (echoedRow.State == "accepted" || echoedRow.State == "dropped")
                    return;
                echoedRow.Echo = true;
                if (JsonNum.Bool(op, "release") == true && echoedRow.State == "sent")
                {
                    echoedRow.State = "echo_observed";
                    // A drop fence stays a drop fence. Echo must not reopen unnamed accepts.
                    if (_fence != OutboundFence.Drop)
                    {
                        _fence = OutboundFence.Echo;
                        _fenceNote = "echo_observed fenced this session; not accepted; reconnect before the next chat";
                    }
                }

                break;
            case "resolve":
                if (Json.Str(op, "client_msg_id") is not { } resolved || !_out.TryGetValue(resolved, out var item))
                    return;
                if (item.State is not ("sent" or "uncertain" or "rate_limited" or "echo_observed"))
                    return;
                if (Json.Str(op, "decision") == "requeue")
                {
                    item.State = "queued";
                    item.Attempt = 0;
                    item.NotBeforeMs = null;
                    item.Echo = false;
                    return;
                }

                if (Json.Str(op, "decision") != "drop")
                    return;
                item.State = "dropped";
                item.NotBeforeMs = null;
                // Tombstone. Later accepted frames on this connection do not complete another row.
                _fence = OutboundFence.Drop;
                _fenceNote = "dropped send fenced this session; reconnect before the next chat";
                break;
            case "accept_open":
                _fence = OutboundFence.None;
                _fenceNote = null;
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
        public int Attempt { get; set; }
        public long? NotBeforeMs { get; set; }
        public bool Echo { get; set; }
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

    private bool AcceptedBlockedByFence(string? clientMsgId)
    {
        if (_fence == OutboundFence.None)
            return false;
        if (_fence == OutboundFence.Drop)
            return true;
        return !IsEchoObserved(clientMsgId);
    }

    private bool IsEchoObserved(string? clientMsgId)
        => clientMsgId is not null
            && _out.TryGetValue(clientMsgId, out var row)
            && row.State == "echo_observed";

    private bool SafeNamedEchoReceipt(JsonObject op)
        => _fence == OutboundFence.Echo && IsEchoObserved(Json.Str(op, "client_msg_id"));

    private void AppendOutbound(IReadOnlyList<JsonObject> ops)
    {
        CatchUpOutbound();
        if (_fence != OutboundFence.None)
        {
            // Drop: persist no accepted line. Echo: persist only a receipt that names the
            // echo_observed row. Anything else would complete a different chat.
            var kept = new List<JsonObject>(ops.Count);
            foreach (var op in ops)
            {
                if (Json.Str(op, "op") == "accepted" && !SafeNamedEchoReceipt(op))
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
