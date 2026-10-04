using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBridge;

internal sealed class HackChatBridge
{
    private const int MaxMessageBytes = 1 << 20;

    private readonly RelayConfig _cfg;
    private readonly BridgeRuntime _runtime;
    private readonly Func<TimeSpan, IRelaySocket> _socketFactory;
    private readonly string _inbox;
    private readonly string _unread;
    private readonly string _state;
    private readonly OutboxReader _outbox;
    private readonly DurableOutbox? _durableOutbox;
    private readonly object _fileLock = new();
    private readonly AutoAcker _acker;
    private readonly MentionRouter? _mentions;
    private readonly IHivemindMirror? _mirror;
    private readonly bool _voizle;
    // Null unless protocol_v2 is on and this URL speaks voizle. v1 sessions never open it.
    private readonly V2Client? _v2;
    // Auto-acks waiting to go out. Filled by the receive loop, drained by the send loop ahead of the outbox,
    // which _sendWake wakes early so an ack doesn't wait out the outbox poll.
    private readonly ConcurrentQueue<JsonObject> _acks = new();
    private readonly SemaphoreSlim _sendWake = new(0, 1);
    // 1 only after onlineSet, and only until this session ends. Backoff in RunForeverAsync
    // runs with this cleared, so a quiet-socket timeout cannot pile reconnects.
    private int _idleArmed;
    private int _idleFired;
    private long _lastInboundTicks;

    public HackChatBridge(RelayConfig cfg, BridgeRuntime? runtime = null)
    {
        _cfg = cfg;
        _runtime = runtime ?? BridgeRuntime.For(cfg);
        _socketFactory = _runtime.SocketFactory ?? (timeout => new ClientRelaySocket(cfg.Origin, timeout));
        Directory.CreateDirectory(cfg.BaseDir);
        _inbox = Path.Combine(cfg.BaseDir, "inbox.jsonl");
        _unread = Path.Combine(cfg.BaseDir, "unread.jsonl");
        _state = Path.Combine(cfg.BaseDir, "state.json");
        // Lines already in the outbox when the process starts are not replayed. The position then lives
        // for the whole process, across reconnects.
        _outbox = OutboxReader.AtEnd(Path.Combine(cfg.BaseDir, "outbox.jsonl"));
        _durableOutbox = cfg.DurableOutbox ? new DurableOutbox(cfg.BaseDir) : null;
        _acker = new AutoAcker(cfg.AutoAck, cfg.Nick);
        _voizle = cfg.SpeaksVoizle;
        // Mention inboxes sit beside inbox.jsonl. Off unless mentions.enabled, so a current
        // deployment keeps the jsonl bridge and does not grow an agents/ tree.
        _mentions = cfg.MentionRouting.Enabled ? MentionRouter.Open(cfg) : null;
        _v2 = cfg.ProtocolV2 && _voizle
            ? V2Client.Open(cfg.BaseDir, V2OutboundOptions.From(cfg), () => _runtime.UtcNow())
            : null;
        if (_v2 is not null)
            _v2.Consumers = new V2WakeQueue(cfg, _fileLock);
        // Room-chat mirror into HIVEMIND. Off unless HIVEMIND_MESSAGE_MIRROR=1.
        // Offer only enqueues; a failure must not affect this session.
        _mirror = HivemindMirror.Open(_runtime, line => TryStderr("[chatbridge] " + line), _cfg.BaseDir);
    }

    private sealed class Session
    {
        public DateTimeOffset? OpenedAt;
        public volatile bool Confirmed;
        public bool HelloAccepted;
        public string? EndReason;
        // Null on the v1 path. Set from the hello when protocol_v2 is on.
        public V2Plan? Plan;
        // Null result: a v1 hello was accepted. Otherwise a rejection reason, without the "join rejected:" prefix.
        public TaskCompletionSource<string?> Hello { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Uptime => OpenedAt is { } t ? DateTimeOffset.UtcNow - t : TimeSpan.Zero;
    }

    public async Task RunForeverAsync(CancellationToken ct)
    {
        // A bad URL cannot start working on the next try. Fail before the loop so the process exits
        // (Main maps ConfigException to exit 2) instead of retrying it forever.
        if (RelayUrl.PermanentProblem(_cfg.Url) is { } problem)
            throw new ConfigException($"{_cfg.ConfigPath}: {problem}");

        var backoff = new Backoff();
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            var session = new Session();
            try
            {
                await RunOnceAsync(session, attempt, ct);
            }
            catch (ConfigException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // DNS, TLS, a refused connection, a hung handshake, a join warn, a close during
                // the join: all transient. None of them ends the process.
                session.EndReason ??= Redact(ex.Message);
            }

            if (ct.IsCancellationRequested)
                break;

            var uptime = session.Uptime;
            var baseDelay = backoff.NextDelaySeconds(session.Confirmed, uptime);
            var delay = Backoff.WithJitter(baseDelay, SafeJitter());
            var reason = Redact(session.EndReason ?? "session ended");
            // Logging and state.json are how an operator sees the retry. A failure here (disk full,
            // inbox path replaced, stderr closed) must not be the thing that kills the bridge.
            try
            {
                LogEvent("err", new JsonObject
                {
                    ["error"] = reason,
                    ["joined"] = session.Confirmed,
                    ["uptime_s"] = (long)uptime.TotalSeconds,
                    ["attempt"] = attempt
                });
            }
            catch (Exception ex)
            {
                TryStderr($"[chatbridge] couldn't log disconnect: {Redact(ex.Message)}");
            }

            TryStderr(
                $"[chatbridge] disconnect: {reason} (joined: {(session.Confirmed ? "yes" : "no")}, up {(long)uptime.TotalSeconds}s, attempt {attempt})");
            try
            {
                WriteState(alive: false, connected: false, reconnecting: true, reason);
            }
            catch (Exception ex)
            {
                TryStderr($"[chatbridge] couldn't write state.json: {Redact(ex.Message)}");
            }

            // stdout can be a closed pipe. That is not a reason to give up on the channel.
            // receive-idle is disarmed for this entire wait. RunOnceAsync arms it only after
            // onlineSet and disarms it before returning, including when the socket went quiet.
            // It must not fire during backoff (a maintenance window would otherwise pile reconnects).
            TryStdout($"[chatbridge] reconnect in {delay.TotalSeconds:0.#}s (attempt {attempt})…");
            try
            {
                await _runtime.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                TryStderr($"[chatbridge] delay failed: {Redact(ex.Message)}");
            }
        }

        try
        {
            WriteState(alive: false, connected: false, reconnecting: false);
        }
        catch (Exception ex)
        {
            TryStderr($"[chatbridge] couldn't write state.json: {Redact(ex.Message)}");
        }

        TryStdout("[chatbridge] stopped");
    }

    private double SafeJitter()
    {
        try
        {
            var unit = _runtime.JitterUnit();
            if (double.IsNaN(unit) || unit < 0)
                return 0;
            return unit > 1 ? 1 : unit;
        }
        catch
        {
            return 0.5;
        }
    }

    private string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "session ended";
        if (!string.IsNullOrEmpty(_cfg.Pass))
            text = text.Replace(_cfg.Pass, LogRedaction.Redacted, StringComparison.Ordinal);
        return text;
    }

    private static string FormatShort(TimeSpan t) =>
        t.TotalSeconds >= 10 ? $"{t.TotalSeconds:0}s"
        : t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.#}s"
        : $"{t.TotalMilliseconds:0}ms";

    private static void TryStderr(string line) => TryWrite(Console.Error, line);

    private void TryStdout(string line)
    {
        if (_runtime.Stdout is { } write)
        {
            try
            {
                write(line);
            }
            catch
            {
                // a test, or a stand-in writer, failed; keep retrying
            }

            return;
        }

        TryWrite(Console.Out, line);
    }

    private static void TryWrite(TextWriter writer, string line)
    {
        try
        {
            writer.WriteLine(line);
        }
        catch
        {
            // stdout or stderr can be closed; the loop still has to keep trying
        }
    }

    private async Task RunOnceAsync(Session s, int attempt, CancellationToken ct)
    {
        // Disarmed through connect, the join wait, and (via finally) the caller's backoff.
        DisarmReceiveIdle();
        try
        {
            await RunSessionAsync(s, attempt, ct);
        }
        finally
        {
            DisarmReceiveIdle();
            Volatile.Write(ref _idleFired, 0);
        }
    }

    private async Task RunSessionAsync(Session s, int attempt, CancellationToken ct)
    {
        // Acks are best effort and belong to the moment: none carries over from an earlier session.
        while (_acks.TryDequeue(out _)) { }


        await using var ws = _socketFactory(_runtime.ConnectTimeout);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            if (_runtime.ConnectTimeout > TimeSpan.Zero && _runtime.ConnectTimeout != Timeout.InfiniteTimeSpan)
                connectCts.CancelAfter(_runtime.ConnectTimeout);

            Console.WriteLine($"[chatbridge] connecting {RelayUrl.ForLog(_cfg.Url)} (attempt {attempt})…");
            try
            {
                await ws.ConnectAsync(new Uri(_cfg.Url), connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"connect not completed within {FormatShort(_runtime.ConnectTimeout)}");
            }
        }

        s.OpenedAt = DateTimeOffset.UtcNow;

        // One send at a time; the outbox pump and the close share this lock.
        var sendLock = new SemaphoreSlim(1, 1);
        // The receive loop is stopped only by sessionCts, not by shutdown directly, so on shutdown
        // it can still read the server's reply to our close frame.
        using var sessionCts = new CancellationTokenSource();
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionCts.Token);

        // Completed with null once the join is confirmed, or with the text of a rejection.
        var joinResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outTask = Task.CompletedTask;
        var idleTask = Task.CompletedTask;

        var joinTimeout = Task.Delay(_runtime.JoinTimeout, pumpCts.Token)
            .ContinueWith(_ => { }, TaskScheduler.Default);

        // hack.chat speaks second, so the join goes out before the receive loop. Doing it the
        // other way lets onlineSet confirm the session and the following state write clear it.
        Task<string> recvTask;
        if (_voizle)
        {
            recvTask = ReceiveLoopAsync(ws, s, joinResult, sessionCts.Token);
            Console.WriteLine("[chatbridge] connected; waiting for voizle-text-relay hello");
            var helloWait = await Task.WhenAny(s.Hello.Task, recvTask, joinTimeout);
            if (helloWait == s.Hello.Task && s.Hello.Task.Result is null)
            {
                await SendVoizleJoinAsync(ws, sendLock, s, ct);
                if (!s.Confirmed)
                    WriteState(alive: true, connected: false, reconnecting: false);
                var pinned = s.Plan?.Dialect == V2Dialect.Deployed ? "v2 join pinned" : "join sent";
                Console.WriteLine($"[chatbridge] {pinned} for #{_cfg.Channel} as {_cfg.Nick}; waiting for welcome");
            }
            else if (helloWait == s.Hello.Task)
            {
                s.EndReason = $"join rejected: {s.Hello.Task.Result}";
            }
            else if (helloWait == joinTimeout && !ct.IsCancellationRequested)
            {
                s.EndReason = $"join not confirmed within {_runtime.JoinTimeout.TotalSeconds:0}s";
            }
        }
        else
        {
            await SendHackChatJoinAsync(ws, sendLock, ct);
            WriteState(alive: true, connected: false, reconnecting: false);
            Console.WriteLine($"[chatbridge] join sent for #{_cfg.Channel} as {_cfg.Nick}; waiting for onlineSet");
            recvTask = ReceiveLoopAsync(ws, s, joinResult, sessionCts.Token);
        }

        var first = s.EndReason is null
            ? await Task.WhenAny(joinResult.Task, recvTask, joinTimeout)
            : null;

        if (first == joinResult.Task && joinResult.Task.Result is null)
        {
            Console.WriteLine($"[chatbridge] joined #{_cfg.Channel} as {_cfg.Nick}");
            // The outbox is drained only after the join is confirmed, so no line is spent on a socket
            // that isn't in the channel yet. The quiet-socket watchdog starts at the same moment:
            // not while the join is still unconfirmed, and not during reconnect backoff.
            outTask = s.Plan?.Dialect == V2Dialect.Deployed && _v2 is not null
                ? V2OutboxLoopAsync(ws, sendLock, pumpCts.Token)
                : OutboxLoopAsync(ws, sendLock, pumpCts.Token);
            if (_cfg.ReceiveIdle > TimeSpan.Zero)
            {
                ArmReceiveIdle();
                idleTask = ReceiveIdleWatchAsync(s, pumpCts.Token);
                await Task.WhenAny(recvTask, outTask, idleTask);
            }
            else
            {
                await Task.WhenAny(recvTask, outTask);
            }
        }
        else if (first == joinResult.Task)
        {
            s.EndReason = $"join rejected: {joinResult.Task.Result}";
        }
        else if (first == joinTimeout && !ct.IsCancellationRequested)
        {
            s.EndReason = $"join not confirmed within {_runtime.JoinTimeout.TotalSeconds:0}s";
        }

        if (Volatile.Read(ref _idleFired) != 0)
        {
            // On this thread, before Describe(), so a cancelled receive cannot replace the
            // quiet-socket reason. Session teardown below cancels sessionCts after the close frame.
            s.EndReason ??= $"receive_idle: quiet socket, no inbound frame for {FormatIdle(_cfg.ReceiveIdle)}";
            Volatile.Write(ref _idleFired, 0);
        }

        if (recvTask.IsCompleted)
            s.EndReason ??= Describe(recvTask, "receive");
        if (outTask.IsCompleted && outTask.IsFaulted)
        {
            var reason = outTask.Exception!.GetBaseException();
            s.EndReason ??= reason is V2SessionEndException
                ? reason.Message
                : Describe(outTask, "send");
        }
        if (idleTask.IsFaulted)
            s.EndReason ??= Describe(idleTask, "receive-idle");

        // End of session (drop, rejected join, quiet socket, or shutdown): disarm before the close
        // grace and before the caller's backoff, stop the outbox pump, send a close frame if the
        // socket is still up, give the server a moment to answer it, then stop the receive loop.
        DisarmReceiveIdle();
        pumpCts.Cancel();
        await TryCloseOutputAsync(ws, sendLock, _runtime.CloseGrace);
        sessionCts.CancelAfter(_runtime.CloseGrace);
        try
        {
            await Task.WhenAll(recvTask, outTask, idleTask);
        }
        catch
        {
            // reasons were collected above
        }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        throw new IOException(s.EndReason ?? "session ended");
    }

    private static string Describe(Task t, string what)
    {
        if (t is Task<string> { IsCompletedSuccessfully: true } done)
            return done.Result;
        if (t.IsFaulted)
            return $"{what} failed: {t.Exception!.GetBaseException().Message}";
        if (t.IsCanceled)
            return $"{what} cancelled";
        return $"{what} ended";
    }

    private async Task SendHackChatJoinAsync(IRelaySocket ws, SemaphoreSlim sendLock, CancellationToken ct)
    {
        var join = new JsonObject { ["cmd"] = "join", ["channel"] = _cfg.Channel, ["nick"] = _cfg.Nick };
        var wireJoin = (JsonObject)join.DeepClone();
        if (!string.IsNullOrEmpty(_cfg.Pass))
            wireJoin["pass"] = _cfg.Pass;
        await SendAsync(ws, sendLock, wireJoin, ct);
        LogEvent("out", join); // logged without the pass
    }

    private async Task SendVoizleJoinAsync(IRelaySocket ws, SemaphoreSlim sendLock, Session s, CancellationToken ct)
    {
        JsonObject wire;
        if (s.Plan?.Dialect == V2Dialect.Deployed)
        {
            // §11: pin v2 on this connection only. The owner secret is pass, never the trip.
            wire = V2Client.JoinFrame(_cfg.Channel, _cfg.Nick, PublicTrip.ForJoin(_cfg.Trip), _cfg.InboxOwnerSecret);
        }
        else
        {
            wire = new JsonObject
            {
                ["v"] = 1,
                ["type"] = "join",
                ["room"] = _cfg.Channel,
                ["nick"] = _cfg.Nick
            };
            if (PublicTrip.ForJoin(_cfg.Trip) is { } trip)
                wire["trip"] = trip;
        }

        await SendAsync(ws, sendLock, wire, ct);
        var logged = (JsonObject)wire.DeepClone();
        LogRedaction.RedactOwnerFields(logged);
        LogEvent("out", InboundFrame.ForLog(logged));
    }

    private async Task<string> ReceiveLoopAsync(
        IRelaySocket ws, Session s, TaskCompletionSource<string?> joinResult, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var assembler = new MessageAssembler(MaxMessageBytes);

        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.IsClose)
            {
                var status = result.CloseStatus ?? "no status";
                var desc = string.IsNullOrEmpty(result.CloseDescription) ? "" : $" {result.CloseDescription}";
                return $"server closed the connection ({status}{desc})";
            }

            var raw = assembler.Append(buffer.AsSpan(0, result.Count), result.EndOfMessage, out var dropped);
            if (dropped)
            {
                LogEvent("err", new JsonObject { ["error"] = $"inbound message over {MaxMessageBytes} bytes skipped" });
                continue;
            }

            if (raw is not null)
            {
                // Any complete server frame refreshes the idle clock once the watchdog is armed.
                // Chat is not required: info, warn, and presence frames are traffic too.
                NoteInboundFrame();
                try
                {
                    HandleFrame(raw, s, joinResult);
                }
                catch (V2SessionEndException ex)
                {
                    return ex.Message;
                }
            }
        }
    }

    private void HandleFrame(string raw, Session s, TaskCompletionSource<string?> joinResult)
    {
        var frame = InboundFrame.Parse(raw);
        if (_voizle && !s.HelloAccepted)
        {
            if (frame.Object is { } helloObj && frame.Cmd == "hello" && IsVoizleHello(helloObj))
            {
                s.HelloAccepted = true;
                if (_v2 is not null)
                {
                    s.Plan = V2Negotiation.Decide(helloObj, optIn: true);
                    if (s.Plan.Dialect == V2Dialect.Deployed)
                        _v2.BeginDeployed(Path.Combine(_cfg.BaseDir, "outbox.jsonl"));
                    if (s.Plan.Note is { } note)
                        LogEvent("note", new JsonObject { ["v2"] = note });
                }

                s.Hello.TrySetResult(null);
            }
            else
            {
                var why = frame.Cmd is "error" or "warn" && frame.Object is { } err
                    ? Json.Str(err, "text") ?? Json.Str(err, "code") ?? frame.Cmd
                    : $"expected voizle-text-relay hello, got {frame.Cmd ?? "a non-hello frame"}";
                s.Hello.TrySetResult(why);
            }

            LogEvent("in", frame.LogNode);
            return;
        }

        Dispatch(frame, s, joinResult);
    }

    private static bool IsVoizleHello(JsonObject obj)
    {
        if (!string.Equals(Json.Str(obj, "protocol"), "voizle-text-relay", StringComparison.Ordinal))
            return false;
        if (obj["v"] is not JsonValue value)
            return false;
        if (value.TryGetValue<int>(out var i))
            return i == 1;
        return value.TryGetValue<long>(out var l) && l == 1;
    }

    private void Dispatch(InboundFrame frame, Session s, TaskCompletionSource<string?> joinResult)
    {
        // Decide on an auto-ack before the chat reaches inbox.jsonl, so the hook state it reports is the one from
        // before this very message reached the poller.
        var ack = frame is { Cmd: "chat", Object: { } chatObj } && s.Confirmed
            ? _acker.Consider(Json.Str(chatObj, "nick"), Json.Str(chatObj, "trip"), Json.Str(chatObj, "text"),
                DateTimeOffset.UtcNow, () => _cfg.ReadHook(DateTimeOffset.UtcNow))
            : null;
        LogEvent("in", frame.LogNode);
        if (frame.Object is not { } obj)
            return; // logged as raw; nothing else to do

        if (frame.Cmd == "chat" && s.Confirmed)
            _durableOutbox?.ObserveEcho(Json.Str(obj, "nick"), Json.Str(obj, "text"), _cfg.Nick);

        if (s.Plan?.Dialect == V2Dialect.Deployed && _v2 is not null)
        {
            // Replay is dispatched before welcome marks the session confirmed. That echo is not
            // evidence for a send this socket just made.
            if (frame.Cmd == "chat"
                && _v2.ObserveEcho(Json.Str(obj, "nick"), Json.Str(obj, "text"), replay: !s.Confirmed))
                WakeSender();

            var inbound = _v2.OnFrame(obj);
            foreach (var wire in inbound.Send)
                EnqueueWire(wire);
            FlushV2Notes();
            if (inbound.WakeOutbox)
                WakeSender();
            if (inbound.EndSession is { } end)
                throw new V2SessionEndException(end);
        }

        if (ack is { Send: true, Text: { } ackText })
        {
            _acks.Enqueue(new JsonObject { ["cmd"] = "chat", ["text"] = ackText });
            WakeSender();
        }
        else if (ack is { Suppressed: true })
        {
            LogEvent("note", new JsonObject { ["auto_ack"] = "suppressed", ["reason"] = ack.Reason, ["to"] = Json.Str(obj, "nick") });
        }

        switch (frame.Cmd)
        {
            case "onlineSet" when !_voizle:
                s.Confirmed = true;
                joinResult.TrySetResult(null);
                if (obj["users"] is JsonArray users)
                {
                    foreach (var u in users.OfType<JsonObject>())
                    {
                        if (u["isme"] is JsonValue me && me.TryGetValue<bool>(out var isMe) && isMe)
                            _acker.OwnTrip = Json.Str(u, "trip") is { Length: > 0 } t ? t : null;
                    }
                }

                break;

            case "welcome" when _voizle:
                // voizle-text-relay answers join with hello, then welcome (never onlineSet).
                // Replay is history from before this session. Dispatch it while still unconfirmed so
                // auto-ack does not answer those lines, and so a reconnect does not ack them again.
                RememberOwnTrip(obj);
                if (obj["replay"] is JsonArray replay)
                {
                    foreach (var line in replay.OfType<JsonObject>())
                    {
                        var replayed = InboundFrame.Parse(line.ToJsonString(JsonUtil.Opts));
                        if (replayed.Cmd == "chat")
                            Dispatch(replayed, s, joinResult);
                    }
                }

                s.Confirmed = true;
                joinResult.TrySetResult(null);
                break;

            case "error" when _voizle && !s.Confirmed:
                joinResult.TrySetResult(Json.Str(obj, "text") ?? Json.Str(obj, "code") ?? "error");
                break;

            case "warn" when !s.Confirmed:
                // hack.chat answers a failed join (nick taken, rate limit, …) with a warn before any onlineSet.
                joinResult.TrySetResult(Json.Str(obj, "text") ?? "warn");
                break;

            case "chat":
                // The line is already in inbox.jsonl. Mirror after that accept.
                // The helper hashes the channel and drops trip and join secrets.
                try
                {
                    _mirror?.Offer(obj, _cfg.Channel);
                }
                catch (Exception ex)
                {
                    TryStderr($"[chatbridge] hivemind message mirror failed ({ex.GetType().Name})");
                }

                if (_mentions is not null && !MentionIngress.TryAccept(_mentions, RoomMessage.FromFrame(obj, _cfg.Channel), out var routeError))
                    LogEvent("err", new JsonObject { ["error"] = "mention route: " + routeError });

                var nick = Json.Str(obj, "nick");
                if (!string.IsNullOrEmpty(nick) && !string.Equals(nick, _cfg.Nick, StringComparison.Ordinal))
                {
                    AppendJsonl(_unread, new JsonObject
                    {
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["nick"] = nick,
                        ["text"] = Json.Str(obj, "text") ?? "",
                        ["channel"] = Json.Str(obj, "channel") ?? _cfg.Channel
                    }.ToJsonString(JsonUtil.Opts));
                }

                break;
        }

        WriteState(alive: true, connected: s.Confirmed, reconnecting: false);
    }

    private void RememberOwnTrip(JsonObject welcome)
    {
        var trip = Json.Str(welcome, "trip");
        if (string.IsNullOrEmpty(trip) && welcome["users"] is JsonArray users)
        {
            var selfId = Json.Str(welcome, "sessionId");
            var selfNick = Json.Str(welcome, "nick") ?? _cfg.Nick;
            foreach (var u in users.OfType<JsonObject>())
            {
                var idMatch = !string.IsNullOrEmpty(selfId)
                    && string.Equals(Json.Str(u, "sessionId"), selfId, StringComparison.Ordinal);
                var nickMatch = string.Equals(Json.Str(u, "nick"), selfNick, StringComparison.Ordinal);
                if ((idMatch || nickMatch) && Json.Str(u, "trip") is { Length: > 0 } t)
                {
                    trip = t;
                    if (idMatch)
                        break;
                }
            }
        }

        // A welcome with no trip (or a later reconnect that drops it) must clear a trip learned earlier.
        // AutoAcker lives for the process, so leaving the old value would suppress that trip forever.
        _acker.OwnTrip = string.IsNullOrEmpty(trip) ? null : trip;
    }

    private async Task V2OutboxLoopAsync(IRelaySocket ws, SemaphoreSlim sendLock, CancellationToken ct)
    {
        var announcedHold = false;
        var announcedRate = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await DrainV2ControlAsync(ws, sendLock, ct);

            var step = _v2!.NextChat();
            FlushV2Notes();
            if (step.ResetSession)
            {
                // The deployed accepted frame has no client id. Leave this socket before
                // sending the next chat, so a late accepted cannot complete that chat.
                throw new V2SessionEndException("fenced this session; reconnect before the next chat");
            }

            if (step.Hold)
            {
                if (step.RateLimitWait)
                {
                    // The server rejected this row. It stays in the queue and is retried after
                    // the delay. This is not the uncertain hold and it is not acceptance.
                    if (!announcedRate)
                    {
                        announcedRate = true;
                        LogEvent("note", new JsonObject
                        {
                            ["v2"] = "rate_limited; row kept for a bounded retry",
                            ["client_msg_id"] = step.ClientMsgId
                        });
                    }
                }
                else if (!announcedHold)
                {
                    // A missed accepted is not resent: §11 says that can store a second message.
                    // The head stays sent or uncertain until a correlated receipt or reconcile.
                    announcedHold = true;
                    LogEvent("note", new JsonObject
                    {
                        ["v2"] = "uncertain outbound held for reconcile",
                        ["client_msg_id"] = step.ClientMsgId
                    });
                }

                var wait = _runtime.OutboxPoll;
                if (step.Wait is { } sooner && sooner > TimeSpan.Zero && sooner < wait)
                    wait = sooner;
                await _sendWake.WaitAsync(wait, ct);
                continue;
            }

            announcedHold = false;
            announcedRate = false;
            if (step.Frame is { } frame)
            {
                // NextChat fsynced `sent` before returning the frame. A throw here leaves
                // the row uncertain; the next session holds instead of sending it again.
                await SendAsync(ws, sendLock, frame, ct);
                LogEvent("out", LogRedaction.Outbound(InboundFrame.ForLog(frame)));
            }

            await _sendWake.WaitAsync(_runtime.OutboxPoll, ct);
        }
    }

    private async Task DrainV2ControlAsync(IRelaySocket ws, SemaphoreSlim sendLock, CancellationToken ct)
    {
        while (_acks.TryDequeue(out var frame))
        {
            var type = Json.Str(frame, "type") ?? Json.Str(frame, "cmd");
            if (type is "pull" or "ack")
            {
                await SendAsync(ws, sendLock, frame, ct);
                LogEvent("out", LogRedaction.Outbound(InboundFrame.ForLog(frame)));
                continue;
            }

            // Auto-ack chats join the durable queue. They do not bypass an uncertain head.
            if (Json.Str(frame, "text") is { } text)
                _v2!.EnqueueLocal(text);
        }
    }

    private void EnqueueWire(JsonObject frame)
    {
        _acks.Enqueue(frame);
        WakeSender();
    }

    private void FlushV2Notes()
    {
        if (_v2 is null)
            return;
        foreach (var note in _v2.TakeNotes())
            LogEvent("note", new JsonObject { ["v2"] = note });
    }

    private async Task OutboxLoopAsync(IRelaySocket ws, SemaphoreSlim sendLock, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            while (_acks.TryDequeue(out var ackFrame))
            {
                var wire = _voizle ? VoizleWire.FromOutbox(ackFrame) : ackFrame;
                await SendAsync(ws, sendLock, wire, ct);
                LogEvent("out", _voizle ? InboundFrame.ForLog(wire) : ackFrame, auto: true);
            }

            if (_durableOutbox is not null)
            {
                foreach (var reply in _durableOutbox.Pending())
                {
                    var payload = new JsonObject { ["cmd"] = "chat", ["text"] = reply.Text };
                    var wire = _voizle ? VoizleWire.FromOutbox(payload) : payload;
                    _durableOutbox.BeginSend(reply.Id);
                    try
                    {
                        await SendAsync(ws, sendLock, wire, ct);
                        _durableOutbox.Sent(reply.Id);
                        LogEvent("out", LogRedaction.Outbound(InboundFrame.ForLog(wire)));
                    }
                    catch
                    {
                        _durableOutbox.Uncertain(reply.Id);
                        throw;
                    }
                }
            }

            IReadOnlyList<OutboxLine> pending;
            try
            {
                pending = _outbox.ReadPending();
            }
            catch (IOException ex)
            {
                LogEvent("err", new JsonObject { ["error"] = $"outbox read: {ex.Message}" });
                pending = Array.Empty<OutboxLine>();
            }

            foreach (var line in pending)
            {
                var payloads = OutboxPayload.BuildAll(line.Text);
                if (payloads.Count == 0 && line.Text.Trim().Length != 0)
                {
                    // Fail-closed: a malformed or mixed outbox line is dropped and logged, never
                    // sent verbatim. The log is a character count only — the line can still hold
                    // a pass or token. (2026-09-27: two envelopes concatenated on one line went
                    // out as raw JSON under the bridge nick.)
                    LogEvent("err", new JsonObject { ["error"] = LogRedaction.DroppedOutboxLine(line.Text) });
                }

                foreach (var payload in payloads)
                {
                    // If this throws, the position hasn't moved past the line: the session ends and the
                    // line is sent again on the next connection (a split line may resend an already-sent
                    // part; duplicates are preferable to loss here, as before).
                    var wire = _voizle ? VoizleWire.FromOutbox(payload) : payload;
                    await SendAsync(ws, sendLock, wire, ct);
                    LogEvent("out", LogRedaction.Outbound(_voizle ? InboundFrame.ForLog(wire) : payload));
                }

                _outbox.Commit(line);
            }

            await _sendWake.WaitAsync(_runtime.OutboxPoll, ct);
        }
    }

    private void WakeSender()
    {
        try
        {
            if (_sendWake.CurrentCount == 0)
                _sendWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signalled
        }
    }

    private static async Task SendAsync(IRelaySocket ws, SemaphoreSlim sendLock, JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonUtil.Opts));
        await sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task TryCloseOutputAsync(IRelaySocket ws, SemaphoreSlim sendLock, TimeSpan grace)
    {
        try
        {
            if (!await sendLock.WaitAsync(grace))
                return;
            try
            {
                if (ws.CanCloseOutput)
                {
                    using var timeout = new CancellationTokenSource(grace);
                    await ws.CloseOutputAsync(timeout.Token);
                }
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch
        {
            // best effort: the socket may already be gone
        }
    }

    private void LogEvent(string dir, JsonNode msg, bool auto = false)
    {
        var row = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["dir"] = dir,
            ["msg"] = msg.Parent is null ? msg : msg.DeepClone()
        };
        if (auto)
            row["auto"] = "ack"; // sent by the bridge itself, not by the agent
        AppendJsonl(_inbox, row.ToJsonString(JsonUtil.Opts));
    }

    private void AppendJsonl(string path, string line)
    {
        lock (_fileLock)
        {
            File.AppendAllText(path, line + "\n");
        }
    }

    private void ArmReceiveIdle()
    {
        var now = _runtime.UtcNow().UtcTicks;
        long seen;
        do
        {
            seen = Interlocked.Read(ref _lastInboundTicks);
            if (seen >= now)
                break;
        } while (Interlocked.CompareExchange(ref _lastInboundTicks, now, seen) != seen);

        Volatile.Write(ref _idleArmed, 1);
    }

    private void NoteInboundFrame()
    {
        // Stamps are kept even before the watchdog is armed (the confirming onlineSet arrives first).
        // ArmReceiveIdle starts the window at confirmation; the watch itself checks _idleArmed.
        Interlocked.Exchange(ref _lastInboundTicks, _runtime.UtcNow().UtcTicks);
    }

    private void DisarmReceiveIdle() => Volatile.Write(ref _idleArmed, 0);

    private async Task ReceiveIdleWatchAsync(Session s, CancellationToken ct)
    {
        var limit = _cfg.ReceiveIdle;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Volatile.Read(ref _idleArmed) == 0 || !s.Confirmed)
                    return;

                var lastTicks = Interlocked.Read(ref _lastInboundTicks);
                if (Volatile.Read(ref _idleArmed) == 0)
                    return;
                // Ticks are unset only if arming lost the race with a cleared stamp. Waiting the
                // full window is the safe side: returning here would end a live session.
                if (lastTicks <= 0)
                {
                    await _runtime.IdleDelay(limit, ct);
                    continue;
                }

                var idleFor = _runtime.UtcNow() - new DateTimeOffset(lastTicks, TimeSpan.Zero);
                var remaining = limit - idleFor;
                if (remaining <= TimeSpan.Zero)
                {
                    if (Volatile.Read(ref _idleArmed) == 0 || !s.Confirmed)
                        return;
                    // The session thread records EndReason; teardown then cancels sessionCts.
                    Volatile.Write(ref _idleFired, 1);
                    return;
                }

                await _runtime.IdleDelay(remaining, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Session ended, or shutdown. Not a quiet socket.
        }
    }

    private static string FormatIdle(TimeSpan limit)
    {
        var seconds = limit.TotalSeconds;
        return Math.Abs(seconds - Math.Round(seconds)) < 0.001 ? $"{seconds:0}s" : $"{seconds:0.###}s";
    }

    private void WriteState(bool alive, bool connected, bool reconnecting, string? reason = null)
    {
        var obj = new JsonObject
        {
            ["alive"] = alive,
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["channel"] = _cfg.Channel,
            ["nick"] = _cfg.Nick,
            ["connected"] = connected,
            ["reconnecting"] = reconnecting
        };
        // Only while reconnecting, and only the redacted end reason. The trip password never lands here.
        if (reconnecting && !string.IsNullOrEmpty(reason))
            obj["reason"] = Redact(reason);
        obj["pid"] = Environment.ProcessId;

        var json = obj.ToJsonString(JsonUtil.Opts);

        // Write-then-rename so a reader never sees a half-written state.json.
        var tmp = _state + ".tmp";
        lock (_fileLock)
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, _state, overwrite: true);
        }
    }
}
