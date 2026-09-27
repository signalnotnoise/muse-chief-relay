using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chief.Bridge;

internal sealed class HackChatBridge
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OutboxPoll = TimeSpan.FromMilliseconds(350);
    private const int MaxMessageBytes = 1 << 20;

    private readonly RelayConfig _cfg;
    private readonly string _inbox;
    private readonly string _unread;
    private readonly string _state;
    private readonly OutboxReader _outbox;
    private readonly object _fileLock = new();
    private readonly AutoAcker _acker;
    // Auto-acks waiting to go out. Filled by the receive loop, drained by the send loop ahead of the outbox,
    // which _sendWake wakes early so an ack doesn't wait out the outbox poll.
    private readonly ConcurrentQueue<JsonObject> _acks = new();
    private readonly SemaphoreSlim _sendWake = new(0, 1);

    public HackChatBridge(RelayConfig cfg)
    {
        _cfg = cfg;
        Directory.CreateDirectory(cfg.BaseDir);
        _inbox = Path.Combine(cfg.BaseDir, "inbox.jsonl");
        _unread = Path.Combine(cfg.BaseDir, "unread.jsonl");
        _state = Path.Combine(cfg.BaseDir, "state.json");
        // Lines already in the outbox when the process starts are not replayed. The position then lives
        // for the whole process, across reconnects.
        _outbox = OutboxReader.AtEnd(Path.Combine(cfg.BaseDir, "outbox.jsonl"));
        _acker = new AutoAcker(cfg.AutoAck, cfg.Nick);
    }

    private sealed class Session
    {
        public DateTimeOffset? OpenedAt;
        public volatile bool Confirmed;
        public string? EndReason;

        public TimeSpan Uptime => OpenedAt is { } t ? DateTimeOffset.UtcNow - t : TimeSpan.Zero;
    }

    public async Task RunForeverAsync(CancellationToken ct)
    {
        var backoff = new Backoff();
        while (!ct.IsCancellationRequested)
        {
            var session = new Session();
            try
            {
                await RunOnceAsync(session, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                session.EndReason ??= ex.Message;
            }

            if (ct.IsCancellationRequested)
                break;

            var uptime = session.Uptime;
            var delay = backoff.NextDelaySeconds(session.Confirmed, uptime);
            var reason = session.EndReason ?? "session ended";
            LogEvent("err", new JsonObject
            {
                ["error"] = reason,
                ["joined"] = session.Confirmed,
                ["uptime_s"] = (long)uptime.TotalSeconds
            });
            Console.Error.WriteLine(
                $"[chief] disconnect: {reason} (joined: {(session.Confirmed ? "yes" : "no")}, up {(long)uptime.TotalSeconds}s)");
            WriteState(alive: false, connected: false, reconnecting: true);

            Console.WriteLine($"[chief] reconnect in {delay}s…");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        WriteState(alive: false, connected: false, reconnecting: false);
        Console.WriteLine("[chief] stopped");
    }

    private async Task RunOnceAsync(Session s, CancellationToken ct)
    {
        // Acks are best effort and belong to the moment: none carries over from an earlier session.
        while (_acks.TryDequeue(out _)) { }

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", _cfg.Origin);

        Console.WriteLine($"[chief] connecting {_cfg.Url}…");
        await ws.ConnectAsync(new Uri(_cfg.Url), ct);
        s.OpenedAt = DateTimeOffset.UtcNow;

        // ClientWebSocket allows one send at a time; the outbox pump and the close share this lock.
        var sendLock = new SemaphoreSlim(1, 1);
        // The receive loop is stopped only by sessionCts, not by shutdown directly, so on shutdown
        // it can still read the server's reply to our close frame.
        using var sessionCts = new CancellationTokenSource();
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionCts.Token);

        var join = new JsonObject { ["cmd"] = "join", ["channel"] = _cfg.Channel, ["nick"] = _cfg.Nick };
        var wireJoin = (JsonObject)join.DeepClone();
        if (!string.IsNullOrEmpty(_cfg.Pass))
            wireJoin["pass"] = _cfg.Pass;
        await SendAsync(ws, sendLock, wireJoin, ct);
        LogEvent("out", join); // logged without the pass
        WriteState(alive: true, connected: false, reconnecting: false);
        Console.WriteLine($"[chief] join sent for #{_cfg.Channel} as {_cfg.Nick}; waiting for onlineSet");

        // Completed with null once onlineSet arrives, or with the text of a warn that came first.
        var joinResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recvTask = ReceiveLoopAsync(ws, s, joinResult, sessionCts.Token);
        var outTask = Task.CompletedTask;

        var joinTimeout = Task.Delay(JoinTimeout, pumpCts.Token)
            .ContinueWith(_ => { }, TaskScheduler.Default);
        var first = await Task.WhenAny(joinResult.Task, recvTask, joinTimeout);

        if (first == joinResult.Task && joinResult.Task.Result is null)
        {
            Console.WriteLine($"[chief] joined #{_cfg.Channel} as {_cfg.Nick}");
            // The outbox is drained only after the join is confirmed, so no line is spent on a socket
            // that isn't in the channel yet.
            outTask = OutboxLoopAsync(ws, sendLock, pumpCts.Token);
            await Task.WhenAny(recvTask, outTask);
        }
        else if (first == joinResult.Task)
        {
            s.EndReason = $"join rejected: {joinResult.Task.Result}";
        }
        else if (first == joinTimeout && !ct.IsCancellationRequested)
        {
            s.EndReason = $"join not confirmed within {JoinTimeout.TotalSeconds:0}s";
        }

        if (recvTask.IsCompleted)
            s.EndReason ??= Describe(recvTask, "receive");
        if (outTask.IsCompleted && outTask.IsFaulted)
            s.EndReason ??= Describe(outTask, "send");

        // End of session (drop, rejected join, or shutdown): stop the outbox pump, send a close frame if
        // the socket is still up, give the server a moment to answer it, then stop the receive loop.
        pumpCts.Cancel();
        await TryCloseOutputAsync(ws, sendLock);
        sessionCts.CancelAfter(CloseGrace);
        try
        {
            await Task.WhenAll(recvTask, outTask);
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

    private async Task<string> ReceiveLoopAsync(
        ClientWebSocket ws, Session s, TaskCompletionSource<string?> joinResult, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var assembler = new MessageAssembler(MaxMessageBytes);

        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var status = ws.CloseStatus?.ToString() ?? "no status";
                var desc = string.IsNullOrEmpty(ws.CloseStatusDescription) ? "" : $" {ws.CloseStatusDescription}";
                return $"server closed the connection ({status}{desc})";
            }

            var raw = assembler.Append(buffer.AsSpan(0, result.Count), result.EndOfMessage, out var dropped);
            if (dropped)
            {
                LogEvent("err", new JsonObject { ["error"] = $"inbound message over {MaxMessageBytes} bytes skipped" });
                continue;
            }

            if (raw is not null)
                HandleFrame(raw, s, joinResult);
        }
    }

    private void HandleFrame(string raw, Session s, TaskCompletionSource<string?> joinResult)
    {
        var frame = InboundFrame.Parse(raw);
        // Decide on an auto-ack before the chat reaches inbox.jsonl, so the hook state it reports is the one from
        // before this very message reached the poller.
        var ack = frame is { Cmd: "chat", Object: { } chatObj } && s.Confirmed
            ? _acker.Consider(Json.Str(chatObj, "nick"), Json.Str(chatObj, "trip"), Json.Str(chatObj, "text"),
                DateTimeOffset.UtcNow, () => _cfg.ReadHook(DateTimeOffset.UtcNow))
            : null;
        LogEvent("in", frame.LogNode);
        if (frame.Object is not { } obj)
            return; // logged as raw; nothing else to do

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
            case "onlineSet":
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

            case "warn" when !s.Confirmed:
                // hack.chat answers a failed join (nick taken, rate limit, …) with a warn before any onlineSet.
                joinResult.TrySetResult(Json.Str(obj, "text") ?? "warn");
                break;

            case "chat":
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

    private async Task OutboxLoopAsync(ClientWebSocket ws, SemaphoreSlim sendLock, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            while (_acks.TryDequeue(out var ackFrame))
            {
                await SendAsync(ws, sendLock, ackFrame, ct);
                LogEvent("out", ackFrame, auto: true);
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
                var payload = OutboxPayload.Build(line.Text);
                if (payload is not null)
                {
                    // If this throws, the position hasn't moved past the line: the session ends and the
                    // line is sent again on the next connection.
                    await SendAsync(ws, sendLock, payload, ct);
                    LogEvent("out", LogRedaction.Outbound(payload));
                }

                _outbox.Commit(line);
            }

            await _sendWake.WaitAsync(OutboxPoll, ct);
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

    private static async Task SendAsync(ClientWebSocket ws, SemaphoreSlim sendLock, JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonUtil.Opts));
        await sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task TryCloseOutputAsync(ClientWebSocket ws, SemaphoreSlim sendLock)
    {
        try
        {
            if (!await sendLock.WaitAsync(CloseGrace))
                return;
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(CloseGrace);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
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

    private void WriteState(bool alive, bool connected, bool reconnecting)
    {
        var json = JsonSerializer.Serialize(new
        {
            alive,
            at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            channel = _cfg.Channel,
            nick = _cfg.Nick,
            connected,
            reconnecting,
            pid = Environment.ProcessId
        }, JsonUtil.Opts);

        // Write-then-rename so a reader never sees a half-written state.json.
        var tmp = _state + ".tmp";
        lock (_fileLock)
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, _state, overwrite: true);
        }
    }
}
