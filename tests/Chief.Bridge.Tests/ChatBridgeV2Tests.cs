using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

/// <summary>
/// Draft v2 client. These tests use the contract fixtures and an in-memory socket.
/// They do not open a live relay.
/// </summary>
public class ChatBridgeV2Tests
{
    private const string DeployedHello =
        """{"protocol":"voizle-text-relay","v":1,"type":"hello","durable":true,"durableVersion":2}""";

    private const string DesignedHello =
        """{"protocol":"voizle-text-relay","v":1,"type":"hello","versions":[2,1],"v2":{"features":["inbox","lease","resume"]}}""";

    private const string Welcome =
        """{"v":2,"type":"welcome","nick":"n","replay":[],"inboxAuth":true}""";

    [Fact]
    public void V1_chat_and_v2_delivery_of_one_message_share_the_dedup_key()
    {
        AssertOneOverlap("room-msg-1");
        AssertOneOverlap(null);
    }

    private static void AssertOneOverlap(string? messageId)
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, """
            {
              "channel": "room",
              "nick": "dot",
              "url": "ws://127.0.0.1:9/relay",
              "base": ".",
              "protocol_v2": true,
              "mentions": { "enabled": true },
              "agents": [ { "id": "dot", "nicks": ["dot"] } ]
            }
            """);
        var cfg = RelayConfig.Load(path, false, dir.Path, _ => null);
        Assert.True(cfg.ProtocolV2);

        const string text = "@dot status?";
        const long ts = 1710000000;
        var v1 = new RoomMessage("room", "alex", "Zz99Yy", text, ts, messageId);
        var router = MentionRouter.Open(cfg);
        router.Accept(v1);
        Assert.Single(router.InboxFor("dot").Pending());

        var queued = new V2QueuedDelivery(
            "lease-9",
            1,
            text,
            "alex",
            "Zz99Yy",
            ts,
            messageId);
        var key = MessageIds.Overlap(v1);
        Assert.Equal(key, MessageIds.Overlap("room", queued));
        Assert.StartsWith(messageId is null ? "msg:" : "srv:", key, StringComparison.Ordinal);
        if (messageId is not null)
            Assert.Equal("srv:" + messageId, key);

        var client = V2Client.Open(dir.Path);
        client.Consumers = new V2WakeQueue(cfg, new object());
        var delivery = new JsonObject
        {
            ["type"] = "delivery",
            ["deliveryId"] = "lease-9",
            ["leaseGeneration"] = 1,
            ["nick"] = "alex",
            ["text"] = text,
            ["trip"] = "Zz99Yy",
            ["ts"] = ts
        };
        if (messageId is not null)
            delivery["id"] = messageId;

        var acked = client.OnFrame(delivery);
        var ack = Assert.Single(acked.Send);
        Assert.Equal("ack", ack["type"]!.GetValue<string>());
        Assert.Equal("lease-9", ack["deliveryId"]!.GetValue<string>());
        Assert.Equal(1, Num(ack["leaseGeneration"]));

        var chat = new JsonObject
        {
            ["type"] = "chat",
            ["nick"] = "alex",
            ["text"] = text,
            ["trip"] = "Zz99Yy",
            ["ts"] = ts
        };
        if (messageId is not null)
            chat["id"] = messageId;
        Assert.Empty(client.OnFrame(chat).Send);
        Assert.Empty(client.OnFrame(delivery).Send);

        var pending = MentionRouter.Open(cfg).InboxFor("dot").Pending();
        var filed = Assert.Single(pending);
        Assert.Equal(key, filed.Event.SourceId);

        var room = File.ReadAllText(Path.Combine(dir.Path, "inbox.jsonl"));
        Assert.Contains("\"v2_handoff\":\"lease-9\"", room, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"v2:lease-9\"", room, StringComparison.Ordinal);
        if (messageId is not null)
            Assert.Contains("\"id\":\"" + messageId + "\"", room, StringComparison.Ordinal);
        else
            Assert.DoesNotContain("\"id\":", room, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocol_v2_defaults_off()
    {
        using var dir = new TempDir();
        var path = dir.File("c.json");
        File.WriteAllText(path, """{"channel":"c","nick":"n","url":"ws://127.0.0.1:9/relay"}""");
        var cfg = RelayConfig.Load(path, false, dir.Path, _ => null);
        Assert.False(cfg.ProtocolV2);
        Assert.Null(cfg.InboxOwnerSecret);
    }

    [Fact]
    public void Fixtures_match_the_deployed_encoder_and_leave_designed_frames_unresolved()
    {
        var deployedHello = Fixture("deployed", "hello");
        var optIn = V2Negotiation.Decide(deployedHello, optIn: true);
        Assert.Equal(2, optIn.JoinVersion);
        Assert.Equal(V2Dialect.Deployed, optIn.Dialect);

        var stayed = V2Negotiation.Decide(deployedHello, optIn: false);
        Assert.Equal(1, stayed.JoinVersion);
        Assert.Equal(V2Dialect.V1, stayed.Dialect);

        var designed = V2Negotiation.Decide(Fixture("unresolved", "designed-hello"), optIn: true);
        Assert.Equal(1, designed.JoinVersion);
        Assert.Equal(V2Dialect.V1, designed.Dialect);
        Assert.Contains("future work", designed.Note, StringComparison.Ordinal);

        var both = (JsonObject)deployedHello.DeepClone();
        both["versions"] = new JsonArray(2, 1);
        both["v2"] = new JsonObject
        {
            ["features"] = new JsonArray("inbox", "lease", "resume")
        };
        var pinned = V2Negotiation.Decide(both, optIn: true);
        Assert.Equal(2, pinned.JoinVersion);
        Assert.Equal(V2Dialect.Deployed, pinned.Dialect);
        Assert.Contains("deployed dialect", pinned.Note, StringComparison.Ordinal);

        var join = V2Client.JoinFrame("<room>", "<nick>", "!<code>", "<owner-secret>");
        AssertSameShape(Fixture("deployed", "join"), join);
        Assert.False(join.ContainsKey("binding"));
        Assert.False(join.ContainsKey("client_msg_id"));

        var chat = V2Client.ChatFrame("<text>");
        Assert.Equal(2, chat["v"]!.GetValue<int>());
        Assert.Equal("chat", chat["type"]!.GetValue<string>());
        Assert.False(chat.ContainsKey("client_msg_id"));

        AssertSameShape(Fixture("deployed", "pull"), V2Client.PullFrame());
        AssertSameShape(Fixture("deployed", "ack"), V2Client.AckFrame("<delivery-id>", 1));

        var designedAck = Fixture("unresolved", "designed-ack");
        Assert.True(designedAck.ContainsKey("lease"));
        Assert.False(V2Client.AckFrame("d", 1).ContainsKey("lease"));

        foreach (var id in new[] { "welcome", "delivery", "pull_result", "ack_result_processed", "ack_result_idempotent", "accepted", "dead", "lease_fenced", "lease_expired" })
            Assert.NotNull(Fixture("deployed", id));
        foreach (var id in new[] { "designed-hello", "designed-bind", "designed-bound", "designed-chat", "designed-leased", "designed-ack", "designed-acked", "designed-resume" })
            Assert.NotNull(Fixture("unresolved", id));
    }

    [Fact]
    public void Binding_token_is_not_ownership_and_a_trip_does_not_gate_ack()
    {
        using var dir = new TempDir();
        var client = Ready(dir.Path);
        var bound = Fixture("unresolved", "designed-bound");
        var ignored = client.OnFrame(bound);
        Assert.Empty(ignored.Send);
        Assert.Contains("designed", client.TakeNotes().Single(), StringComparison.Ordinal);
        Assert.False(File.Exists(client.InboundPath));

        var logged = InboundFrame.Parse(bound.ToJsonString());
        Assert.Equal(LogRedaction.Redacted, logged.LogNode!["binding"]!.GetValue<string>());
        Assert.DoesNotContain("<opaque-token>", logged.LogNode.ToJsonString(), StringComparison.Ordinal);

        var delivery = Fixture("deployed", "delivery");
        delivery["trip"] = "Zz99Yy";
        delivery["text"] = "hello from fixture";
        delivery["nick"] = "alex";
        var acked = client.OnFrame(delivery);
        Assert.Equal("<delivery-id>", acked.Send.Single()["deliveryId"]!.GetValue<string>());
    }

    [Fact]
    public void Enqueue_is_durable_before_send_and_before_ack()
    {
        using var dir = new TempDir();
        var outbox = dir.File("outbox.jsonl");
        File.WriteAllText(outbox, "");
        var client = V2Client.Open(dir.Path);
        client.BeginDeployed(outbox);
        File.AppendAllText(outbox, """{"text":"hello room"}""" + "\n");

        client.ImportNewOutboxLines();
        var queued = File.ReadAllText(client.OutboundPath);
        Assert.Contains("\"op\":\"enqueue\"", queued, StringComparison.Ordinal);
        Assert.Contains("\"client_msg_id\"", queued, StringComparison.Ordinal);
        Assert.DoesNotContain("\"op\":\"sent\"", queued, StringComparison.Ordinal);

        var step = client.NextChat();
        Assert.True(step.Send);
        Assert.False(step.Frame!.ContainsKey("client_msg_id"));
        Assert.Equal("hello room", step.Frame["text"]!.GetValue<string>());
        Assert.Contains("\"op\":\"sent\"", File.ReadAllText(client.OutboundPath), StringComparison.Ordinal);
        Assert.NotNull(step.ClientMsgId);
        Assert.Contains(step.ClientMsgId, queued, StringComparison.Ordinal);

        AcceptQueue? queue = null;
        queue = new AcceptQueue
        {
            OnEnqueue = () =>
            {
                var mid = File.ReadAllText(client.InboundPath);
                Assert.Contains("\"op\":\"seen\"", mid, StringComparison.Ordinal);
                Assert.Contains("hello from fixture", mid, StringComparison.Ordinal);
                Assert.DoesNotContain("\"op\":\"handed_off\"", mid, StringComparison.Ordinal);
                Assert.DoesNotContain("\"op\":\"ack_pending\"", mid, StringComparison.Ordinal);
            }
        };
        client.Consumers = queue;
        var delivery = client.OnFrame(Delivery(1));
        Assert.Equal(1, queue.Calls);
        var inbound = File.ReadAllText(client.InboundPath);
        var seenAt = inbound.IndexOf("\"op\":\"seen\"", StringComparison.Ordinal);
        var handAt = inbound.IndexOf("\"op\":\"handed_off\"", StringComparison.Ordinal);
        var pendingAt = inbound.IndexOf("\"op\":\"ack_pending\"", StringComparison.Ordinal);
        Assert.True(seenAt >= 0 && handAt > seenAt && pendingAt > handAt);
        Assert.Equal("ack", delivery.Send.Single()["type"]!.GetValue<string>());
        Assert.Equal(1, Num(delivery.Send.Single()["leaseGeneration"]));
    }

    [Fact]
    public void Processed_generation_is_deduped_and_seen_or_fenced_can_be_released()
    {
        using var dir = new TempDir();
        var queue = new AcceptQueue();
        var client = Ready(dir.Path, queue);

        var first = client.OnFrame(Delivery(1));
        Assert.Single(first.Send);
        Assert.Equal(1, queue.Calls);
        var again = client.OnFrame(Delivery(1));
        Assert.Empty(again.Send);
        Assert.Equal(1, queue.Calls);
        Assert.DoesNotContain("\"op\":\"processed\"", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);

        var done = client.OnFrame(Fixture("deployed", "ack_result_processed"));
        Assert.Contains("\"how\":\"processed\"", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);
        Assert.Empty(client.OnFrame(Delivery(1)).Send);

        var next = client.OnFrame(Delivery(2));
        Assert.Equal(2, Num(next.Send.Single()["leaseGeneration"]));
        Assert.Equal(2, queue.Calls);

        using var fencedDir = new TempDir();
        var fencedQueue = new AcceptQueue();
        var fenced = Ready(fencedDir.Path, fencedQueue);
        Assert.Single(fenced.OnFrame(Delivery(1)).Send);
        var fencedResult = fenced.OnFrame(Fixture("deployed", "lease_fenced"));
        Assert.Empty(fencedResult.Send);
        Assert.DoesNotContain("\"op\":\"processed\"", File.ReadAllText(fenced.InboundPath), StringComparison.Ordinal);
        var redelivered = fenced.OnFrame(Delivery(1));
        Assert.Equal(1, Num(redelivered.Send.Single()["leaseGeneration"]));
        Assert.Equal(2, fencedQueue.Calls);

        using var idemDir = new TempDir();
        var idem = Ready(idemDir.Path);
        Assert.Single(idem.OnFrame(Delivery(4)).Send);
        idem.OnFrame(Fixture("deployed", "ack_result_idempotent"));
        Assert.Contains("\"how\":\"idempotent\"", File.ReadAllText(idem.InboundPath), StringComparison.Ordinal);
        Assert.Empty(idem.OnFrame(Delivery(4)).Send);
    }

    [Fact]
    public void Second_delivery_waits_until_the_in_flight_ack_settles()
    {
        using var dir = new TempDir();
        var client = Ready(dir.Path);
        Assert.Single(client.OnFrame(Delivery(1, "a")).Send);
        Assert.Empty(client.OnFrame(Delivery(1, "b")).Send);
        var settled = client.OnFrame(Fixture("deployed", "ack_result_processed"));
        Assert.Equal("b", settled.Send.Single()["deliveryId"]!.GetValue<string>());
    }

    [Fact]
    public void Pending_inbound_and_outbound_survive_reopen()
    {
        using var dir = new TempDir();
        var outbox = dir.File("outbox.jsonl");
        File.WriteAllText(outbox, "");
        var client = Ready(dir.Path);
        client.BeginDeployed(outbox);
        File.AppendAllText(outbox, """{"text":"keep me"}""" + "\n");
        var sent = client.NextChat();
        Assert.True(sent.Send);
        Assert.Single(client.OnFrame(Delivery(3, "keep")).Send);

        var replayQueue = new AcceptQueue();
        var reopened = Ready(dir.Path, replayQueue);
        reopened.BeginDeployed(outbox);
        var hold = reopened.NextChat();
        Assert.True(hold.Hold);
        Assert.False(hold.Send);
        Assert.Equal(sent.ClientMsgId, hold.ClientMsgId);

        var welcome = reopened.OnFrame(JsonNode.Parse(Welcome)!.AsObject());
        var replay = welcome.Send.Single();
        Assert.Equal("ack", replay["type"]!.GetValue<string>());
        Assert.Equal("keep", replay["deliveryId"]!.GetValue<string>());
        Assert.Equal(3, Num(replay["leaseGeneration"]));
        Assert.Equal(0, replayQueue.Calls);

        reopened.OnFrame(Fixture("deployed", "ack_result_processed"));
        var third = V2Client.Open(dir.Path);
        var after = third.OnFrame(JsonNode.Parse(Welcome)!.AsObject());
        Assert.DoesNotContain(after.Send, frame => Json.Str(frame, "type") == "ack");
        Assert.Empty(third.OnFrame(Delivery(3, "keep")).Send);
    }

    [Fact]
    public void Uncertain_send_holds_and_does_not_skip_the_queue_on_reopen()
    {
        using var dir = new TempDir();
        var outbox = dir.File("outbox.jsonl");
        File.WriteAllText(outbox, "{\"text\":\"already there\"}\n{\"text\":\"partial");
        var client = V2Client.Open(dir.Path);
        client.BeginDeployed(outbox);
        File.AppendAllText(outbox, "\"}\n");
        var finished = client.NextChat();
        Assert.Equal("partial", finished.Frame!["text"]!.GetValue<string>());
        Assert.True(client.ResolveUncertain(finished.ClientMsgId!, "drop"));
        var fenced = client.NextChat();
        Assert.False(fenced.Send);
        Assert.True(fenced.ResetSession);

        File.AppendAllText(outbox, """{"text":"in flight"}""" + "\n");
        Assert.False(client.NextChat().Send);
        client.BeginDeployed(outbox);
        var inflight = client.NextChat();
        Assert.Equal("in flight", inflight.Frame!["text"]!.GetValue<string>());
        File.AppendAllText(outbox, """{"text":"behind"}""" + "\n");

        var reopened = V2Client.Open(dir.Path);
        reopened.BeginDeployed(outbox);
        var held = reopened.NextChat();
        Assert.True(held.Hold);
        Assert.Null(held.Frame);
        Assert.Contains("behind", File.ReadAllText(reopened.OutboundPath), StringComparison.Ordinal);

        Assert.True(reopened.ResolveUncertain(held.ClientMsgId!, "drop"));
        Assert.True(reopened.NextChat().ResetSession);
        Assert.False(reopened.NextChat().Send);
        reopened.BeginDeployed(outbox);
        var next = reopened.NextChat();
        Assert.Equal("behind", next.Frame!["text"]!.GetValue<string>());

        using var retryDir = new TempDir();
        var retryBox = retryDir.File("outbox.jsonl");
        File.WriteAllText(retryBox, "");
        var retry = V2Client.Open(retryDir.Path);
        retry.BeginDeployed(retryBox);
        File.AppendAllText(retryBox, """{"text":"retry me"}""" + "\n");
        var once = retry.NextChat();
        Assert.True(retry.ResolveUncertain(once.ClientMsgId!, "requeue"));
        var again = retry.NextChat();
        Assert.Equal("retry me", again.Frame!["text"]!.GetValue<string>());
        Assert.Equal(once.ClientMsgId, again.ClientMsgId);
    }

    [Fact]
    public void Accepted_frame_releases_the_hold_and_stores_ids()
    {
        using var dir = new TempDir();
        var outbox = dir.File("outbox.jsonl");
        File.WriteAllText(outbox, "");
        var client = V2Client.Open(dir.Path);
        client.BeginDeployed(outbox);
        client.EnqueueLocal("ping");
        var step = client.NextChat();
        Assert.True(client.NextChat().Hold);

        var accepted = client.OnFrame(Fixture("deployed", "accepted"));
        Assert.True(accepted.WakeOutbox);
        var log = File.ReadAllText(client.OutboundPath);
        Assert.Contains("\"messageId\":\"<message-id>\"", log, StringComparison.Ordinal);
        Assert.Contains("\"ingressId\":\"<ingress-id>\"", log, StringComparison.Ordinal);
        Assert.NotNull(step.ClientMsgId);
        Assert.Contains(step.ClientMsgId, log, StringComparison.Ordinal);
        Assert.False(client.NextChat().Hold);
    }

    [Fact]
    public void Pull_repeats_while_queued_is_positive()
    {
        using var dir = new TempDir();
        var client = V2Client.Open(dir.Path);
        var welcome = client.OnFrame(JsonNode.Parse(Welcome)!.AsObject());
        Assert.Equal("pull", welcome.Send.Single()["type"]!.GetValue<string>());

        var more = client.OnFrame(Fixture("deployed", "pull_result"));
        Assert.Equal("pull", more.Send.Single()["type"]!.GetValue<string>());

        var done = (JsonObject)Fixture("deployed", "pull_result").DeepClone();
        done["queued"] = 0;
        Assert.Empty(client.OnFrame(done).Send);

        var quiet = V2Client.Open(dir.File("other"));
        var noAuth = JsonNode.Parse("""{"type":"welcome","replay":[]}""")!.AsObject();
        Assert.Empty(quiet.OnFrame(noAuth).Send);
    }

    [Fact]
    public async Task Flag_off_keeps_a_v1_join_when_the_server_advertises_durable()
    {
        await using var fx = new RelayFixture { Url = "ws://127.0.0.1:8787/relay" };
        fx.Script.Enqueue(Attempt.HoldBeforeJoin());
        var phase = 0;
        await fx.RunUntil(() =>
        {
            if (fx.Script.Latest is null || fx.Script.Created == 0)
                return false;
            if (phase == 0 && fx.Script.Sent.Count == 0)
            {
                fx.Script.Latest.Push(DeployedHello);
                phase = 1;
                return false;
            }

            if (fx.Script.Sent.Count < 1)
                return false;
            var join = JsonNode.Parse(fx.Script.Sent[0])!.AsObject();
            Assert.Equal(1, join["v"]!.GetValue<int>());
            Assert.Equal("join", join["type"]!.GetValue<string>());
            Assert.False(join.ContainsKey("pass"));
            Assert.False(File.Exists(Path.Combine(fx.Dir.Path, V2Client.OutboundName)));
            Assert.False(File.Exists(Path.Combine(fx.Dir.Path, V2Client.InboundName)));
            Assert.False(Directory.Exists(Path.Combine(fx.Dir.Path, "agents")));
            return true;
        });
    }

    [Fact]
    public async Task Opt_in_pins_v2_acks_a_delivery_and_does_not_resend_an_uncertain_chat()
    {
        await using var fx = new RelayFixture
        {
            Url = "ws://127.0.0.1:8787/relay",
            ProtocolV2 = true
        };
        fx.Script.Enqueue(Attempt.HoldBeforeJoin(), Attempt.HoldBeforeJoin());
        var phase = 0;
        var secondHello = false;

        await fx.RunUntil(() =>
        {
            if (fx.Script.Latest is null)
                return false;

            if (phase == 0 && fx.Script.Created == 1 && fx.Script.Sent.Count == 0)
            {
                fx.Script.Latest.Push(DeployedHello);
                phase = 1;
                return false;
            }

            if (phase == 1 && fx.Script.Sent.Count >= 1)
            {
                var join = JsonNode.Parse(fx.Script.Sent[0])!.AsObject();
                Assert.Equal(2, join["v"]!.GetValue<int>());
                Assert.Equal("join", join["type"]!.GetValue<string>());
                Assert.False(join.ContainsKey("client_msg_id"));
                Assert.False(join.ContainsKey("pass"));
                fx.Script.Latest.Push(Welcome);
                phase = 2;
                return false;
            }

            if (phase == 2 && fx.Script.Sent.Any(IsType("pull")))
            {
                fx.Script.Latest.Push("""{"type":"delivery","deliveryId":"d1","leaseGeneration":1,"nick":"alex","text":"hello from fixture","trip":"Zz99Yy"}""");
                phase = 3;
                return false;
            }

            if (phase == 3 && fx.Script.Sent.Any(IsType("ack")))
            {
                var inbound = File.ReadAllText(Path.Combine(fx.Dir.Path, V2Client.InboundName));
                var seenAt = inbound.IndexOf("\"op\":\"seen\"", StringComparison.Ordinal);
                var handAt = inbound.IndexOf("\"op\":\"handed_off\"", StringComparison.Ordinal);
                var pendingAt = inbound.IndexOf("\"op\":\"ack_pending\"", StringComparison.Ordinal);
                Assert.True(seenAt >= 0 && handAt > seenAt && pendingAt > handAt);
                Assert.Contains("hello from fixture", inbound, StringComparison.Ordinal);
                var room = File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl"));
                Assert.Contains("\"v2_handoff\":\"d1\"", room, StringComparison.Ordinal);
                var agentInbox = File.ReadAllText(Path.Combine(fx.Dir.Path, "agents", "n", "inbox.jsonl"));
                Assert.Contains("hello from fixture", agentInbox, StringComparison.Ordinal);
                var source = MessageIds.Overlap(new RoomMessage("throwaway-test", "alex", "Zz99Yy", "hello from fixture", null, null));
                Assert.Contains("\"source_id\":\"" + source + "\"", agentInbox, StringComparison.Ordinal);
                Assert.DoesNotContain("\"source_id\":\"v2:d1\"", agentInbox, StringComparison.Ordinal);
                var ack = fx.Script.Sent.Select(Parse).Last(o => Json.Str(o, "type") == "ack");
                Assert.Equal("d1", ack["deliveryId"]!.GetValue<string>());
                Assert.Equal(1, Num(ack["leaseGeneration"]));
                fx.Script.Latest.Push("""{"type":"ack_result","state":"processed"}""");
                File.AppendAllText(Path.Combine(fx.Dir.Path, "outbox.jsonl"), """{"text":"only once"}""" + "\n");
                phase = 4;
                return false;
            }

            if (phase == 4)
            {
                var chats = fx.Script.Sent.Select(Parse).Where(o => Json.Str(o, "type") == "chat").ToList();
                if (chats.Count == 0)
                    return false;
                Assert.Single(chats);
                Assert.Equal(2, chats[0]["v"]!.GetValue<int>());
                Assert.Equal("only once", chats[0]["text"]!.GetValue<string>());
                Assert.False(chats[0].ContainsKey("client_msg_id"));
                fx.Script.Latest.Close();
                phase = 5;
                return false;
            }

            if (phase == 5 && fx.Script.Created >= 2)
            {
                if (!secondHello)
                {
                    fx.Script.Latest.Push(DeployedHello);
                    secondHello = true;
                    return false;
                }

                if (fx.Script.Sent.Count(IsJoin) < 2)
                    return false;
                fx.Script.Latest.Push(Welcome);
                phase = 6;
                return false;
            }

            if (phase != 6)
                return false;
            var inbox = Path.Combine(fx.Dir.Path, "inbox.jsonl");
            if (!File.Exists(inbox) || !File.ReadAllText(inbox).Contains("uncertain outbound", StringComparison.Ordinal))
                return false;
            var chatCount = fx.Script.Sent.Select(Parse).Count(o => Json.Str(o, "type") == "chat");
            Assert.Equal(1, chatCount);
            var outbound = File.ReadAllText(Path.Combine(fx.Dir.Path, V2Client.OutboundName));
            Assert.Contains("\"op\":\"sent\"", outbound, StringComparison.Ordinal);
            Assert.DoesNotContain("\"op\":\"accepted\"", outbound, StringComparison.Ordinal);
            return true;
        });
    }

    [Fact]
    public async Task Designed_dual_hello_stays_on_v1_when_durable_version_is_absent()
    {
        await using var fx = new RelayFixture
        {
            Url = "ws://127.0.0.1:8787/relay",
            ProtocolV2 = true
        };
        fx.Script.Enqueue(Attempt.HoldBeforeJoin());
        var sawHello = false;
        await fx.RunUntil(() =>
        {
            if (fx.Script.Latest is null)
                return false;
            if (!sawHello && fx.Script.Sent.Count == 0)
            {
                fx.Script.Latest.Push(DesignedHello);
                sawHello = true;
                return false;
            }

            if (fx.Script.Sent.Count < 1)
                return false;
            var join = JsonNode.Parse(fx.Script.Sent[0])!.AsObject();
            Assert.Equal(1, join["v"]!.GetValue<int>());
            Assert.DoesNotContain(fx.Script.Sent, line => IsType("pull")(line));
            var inbox = File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl"));
            Assert.Contains("future work", inbox, StringComparison.Ordinal);
            return true;
        });
    }

    [Fact]
    public async Task Owner_secret_is_on_the_join_and_redacted_in_the_log()
    {
        const string owner = "fixture-owner";
        await using var fx = new RelayFixture
        {
            Url = "ws://127.0.0.1:8787/relay",
            ProtocolV2 = true,
            Trip = "Ab12Cd",
            InboxOwnerEnv = "CHATBRIDGE_INBOX_OWNER",
            Env = name => name == "CHATBRIDGE_INBOX_OWNER" ? owner : null
        };
        fx.Script.Enqueue(Attempt.HoldBeforeJoin());
        var pushed = false;
        await fx.RunUntil(() =>
        {
            if (fx.Script.Latest is null)
                return false;
            if (!pushed && fx.Script.Sent.Count == 0)
            {
                fx.Script.Latest.Push(DeployedHello);
                pushed = true;
                return false;
            }

            if (fx.Script.Sent.Count < 1)
                return false;
            var join = JsonNode.Parse(fx.Script.Sent[0])!.AsObject();
            Assert.Equal(2, join["v"]!.GetValue<int>());
            Assert.Equal("!Ab12Cd", join["trip"]!.GetValue<string>());
            Assert.Equal(owner, join["pass"]!.GetValue<string>());
            var inbox = File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl"));
            Assert.DoesNotContain(owner, inbox, StringComparison.Ordinal);
            Assert.Contains(LogRedaction.Redacted, inbox, StringComparison.Ordinal);
            return true;
        });
    }

    [Fact]
    public void Enqueue_failure_does_not_ack_and_a_later_delivery_can()
    {
        using var dir = new TempDir();
        var failing = new AcceptQueue { Fail = true };
        var client = Ready(dir.Path, failing);
        var denied = client.OnFrame(Delivery(1, "d1"));
        Assert.Empty(denied.Send);
        Assert.Contains("not acked", string.Join('\n', client.TakeNotes()), StringComparison.Ordinal);
        var inbound = File.ReadAllText(client.InboundPath);
        Assert.Contains("\"op\":\"seen\"", inbound, StringComparison.Ordinal);
        Assert.Contains("hello from fixture", inbound, StringComparison.Ordinal);
        Assert.DoesNotContain("\"op\":\"handed_off\"", inbound, StringComparison.Ordinal);
        Assert.DoesNotContain("\"op\":\"ack_pending\"", inbound, StringComparison.Ordinal);

        var retry = new AcceptQueue();
        var reopened = Ready(dir.Path, retry);
        var welcome = reopened.OnFrame(JsonNode.Parse(Welcome)!.AsObject());
        Assert.DoesNotContain(welcome.Send, frame => Json.Str(frame, "type") == "ack");
        Assert.Equal(0, retry.Calls);

        var acked = reopened.OnFrame(Delivery(1, "d1"));
        Assert.Equal("ack", acked.Send.Single()["type"]!.GetValue<string>());
        Assert.Equal(1, retry.Calls);
        Assert.Contains("\"op\":\"ack_pending\"", File.ReadAllText(reopened.InboundPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Delivery_without_text_is_not_acked()
    {
        using var dir = new TempDir();
        var queue = new AcceptQueue();
        var client = Ready(dir.Path, queue);
        var bare = Fixture("deployed", "delivery");
        Assert.Empty(client.OnFrame(bare).Send);
        Assert.Equal(0, queue.Calls);
        Assert.DoesNotContain("\"op\":\"ack_pending\"", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);
        Assert.Contains("no text payload", string.Join('\n', client.TakeNotes()), StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_message_text_is_the_payload_that_is_queued()
    {
        using var dir = new TempDir();
        var queue = new AcceptQueue();
        var client = Ready(dir.Path, queue);
        var frame = Fixture("deployed", "delivery");
        frame["message"] = new JsonObject { ["text"] = "nested body", ["nick"] = "alex" };
        var acked = client.OnFrame(frame);
        Assert.Equal("ack", acked.Send.Single()["type"]!.GetValue<string>());
        Assert.Equal("nested body", queue.LastText);
        Assert.Contains("nested body", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Ack_result_correlates_delivery_id_and_ignores_a_stale_one()
    {
        using var dir = new TempDir();
        var client = Ready(dir.Path);
        Assert.Single(client.OnFrame(Delivery(1, "a")).Send);
        Assert.Empty(client.OnFrame(Delivery(1, "b")).Send);

        var other = client.OnFrame(Ack("other", 1));
        Assert.Empty(other.Send);
        Assert.DoesNotContain("\"op\":\"processed\"", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);
        Assert.Contains("does not match", string.Join('\n', client.TakeNotes()), StringComparison.Ordinal);

        var wrongGen = client.OnFrame(Ack("a", 9));
        Assert.Empty(wrongGen.Send);
        Assert.DoesNotContain("\"op\":\"processed\"", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);

        var matched = client.OnFrame(Ack("a", 1));
        Assert.Equal("b", matched.Send.Single()["deliveryId"]!.GetValue<string>());
        Assert.Contains("\"how\":\"processed\"", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);
        Assert.Empty(client.OnFrame(Delivery(1, "a")).Send);
    }

    [Fact]
    public void Late_accepted_after_drop_does_not_complete_the_next_chat()
    {
        using var dir = new TempDir();
        var outbox = dir.File("outbox.jsonl");
        File.WriteAllText(outbox, "");
        var client = V2Client.Open(dir.Path);
        client.BeginDeployed(outbox);
        var alpha = client.EnqueueLocal("alpha");
        var sent = client.NextChat();
        Assert.True(sent.Send);
        Assert.Equal(alpha, sent.ClientMsgId);
        var beta = client.EnqueueLocal("beta");

        var resolve = new JsonObject
        {
            ["op"] = "resolve",
            ["client_msg_id"] = alpha,
            ["decision"] = "drop"
        };
        File.AppendAllText(client.OutboundPath, resolve.ToJsonString(JsonUtil.Opts) + "\n");

        var late = client.OnFrame(Fixture("deployed", "accepted"));
        Assert.False(late.WakeOutbox);
        Assert.Contains("accepted ignored", string.Join('\n', client.TakeNotes()), StringComparison.Ordinal);
        var blocked = client.NextChat();
        Assert.False(blocked.Send);
        Assert.True(blocked.ResetSession);

        var log = File.ReadAllText(client.OutboundPath);
        Assert.DoesNotContain("\"op\":\"accepted\"", log, StringComparison.Ordinal);
        Assert.Contains("\"decision\":\"drop\"", log, StringComparison.Ordinal);
        Assert.DoesNotContain(beta, log.Split('\n').Where(line => line.Contains("\"op\":\"accepted\"", StringComparison.Ordinal)));

        client.OnFrame(Fixture("deployed", "accepted"));
        Assert.DoesNotContain("\"op\":\"accepted\"", File.ReadAllText(client.OutboundPath), StringComparison.Ordinal);

        client.BeginDeployed(outbox);
        var betaStep = client.NextChat();
        Assert.True(betaStep.Send);
        Assert.Equal(beta, betaStep.ClientMsgId);
        Assert.Equal("beta", betaStep.Frame!["text"]!.GetValue<string>());
        Assert.NotEqual(alpha, beta);

        client.OnFrame(Fixture("deployed", "accepted"));
        var after = File.ReadAllText(client.OutboundPath);
        Assert.Contains("\"op\":\"accepted\"", after, StringComparison.Ordinal);
        Assert.Contains(beta, after, StringComparison.Ordinal);
        Assert.DoesNotContain("\"op\":\"accepted\",\"client_msg_id\":\"" + alpha + "\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Drop_then_sent_B_then_delayed_accepted_does_not_accept_B()
    {
        using var dir = new TempDir();
        var outbox = dir.File("outbox.jsonl");
        File.WriteAllText(outbox, "");
        var client = V2Client.Open(dir.Path);
        client.BeginDeployed(outbox);
        var alpha = client.EnqueueLocal("alpha");
        Assert.Equal(alpha, client.NextChat().ClientMsgId);
        var beta = client.EnqueueLocal("beta");
        Assert.True(client.ResolveUncertain(alpha, "drop"));

        // The pre-fix pump sent B on this connection, then FirstOpen attached A's accepted to B.
        File.AppendAllText(client.OutboundPath, new JsonObject
        {
            ["op"] = "sent",
            ["client_msg_id"] = beta
        }.ToJsonString(JsonUtil.Opts) + "\n");
        File.AppendAllText(client.OutboundPath, new JsonObject
        {
            ["op"] = "accepted",
            ["client_msg_id"] = beta,
            ["messageId"] = "<message-id>"
        }.ToJsonString(JsonUtil.Opts) + "\n");

        var late = client.OnFrame(Fixture("deployed", "accepted"));
        Assert.False(late.WakeOutbox);
        Assert.Contains(beta, client.UncertainIds());
        Assert.DoesNotContain(alpha, client.UncertainIds());

        var reloaded = V2Client.Open(dir.Path);
        Assert.Contains(beta, reloaded.UncertainIds());
        Assert.DoesNotContain(alpha, reloaded.UncertainIds());
        reloaded.BeginDeployed(outbox);
        var held = reloaded.NextChat();
        Assert.True(held.Hold);
        Assert.Equal(beta, held.ClientMsgId);
        Assert.Null(held.Frame);
        Assert.Contains(beta, reloaded.UncertainIds());
    }

    [Fact]
    public void Operator_resolve_line_is_applied_on_the_next_pump_step()
    {
        using var dir = new TempDir();
        var outbox = dir.File("outbox.jsonl");
        File.WriteAllText(outbox, "");
        var client = V2Client.Open(dir.Path);
        client.BeginDeployed(outbox);
        client.EnqueueLocal("held");
        var sent = client.NextChat();
        Assert.True(sent.Send);
        var line = new JsonObject
        {
            ["op"] = "resolve",
            ["client_msg_id"] = sent.ClientMsgId,
            ["decision"] = "drop"
        };
        File.AppendAllText(client.OutboundPath, line.ToJsonString(JsonUtil.Opts) + "\n");
        var after = client.NextChat();
        Assert.False(after.Hold);
        Assert.False(after.Send);
        Assert.True(after.ResetSession);
        Assert.Empty(client.UncertainIds());
    }

    [Fact]
    public async Task Reconcile_command_drops_a_held_send_and_refuses_a_second_time()
    {
        using var dir = new TempDir();
        var cfg = dir.File("config.json");
        File.WriteAllText(cfg, """{"channel":"c","nick":"n","url":"ws://127.0.0.1:9/relay","base":"."}""");
        var outbox = dir.File("outbox.jsonl");
        File.WriteAllText(outbox, "");
        var client = V2Client.Open(dir.Path);
        client.BeginDeployed(outbox);
        client.EnqueueLocal("held");
        var sent = client.NextChat();
        Assert.NotNull(sent.ClientMsgId);

        var prev = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            Assert.Equal(0, await Program.Main(["reconcile", "--config", cfg, "--id", sent.ClientMsgId, "drop"]));
            Assert.Equal(1, await Program.Main(["reconcile", "--config", cfg, "--id", sent.ClientMsgId, "requeue"]));
        }
        finally
        {
            Console.SetOut(prev);
        }

        Assert.Contains("reconciled", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("held", stdout.ToString(), StringComparison.Ordinal);
        var reopened = V2Client.Open(dir.Path);
        reopened.BeginDeployed(outbox);
        Assert.False(reopened.NextChat().Send);
        Assert.False(reopened.NextChat().Hold);
    }

    [Fact]
    public async Task Opt_in_does_not_ack_when_the_consumer_queue_cannot_be_written()
    {
        await using var fx = new RelayFixture
        {
            Url = "ws://127.0.0.1:8787/relay",
            ProtocolV2 = true
        };
        File.WriteAllText(Path.Combine(fx.Dir.Path, "agents"), "not-a-directory");
        fx.Script.Enqueue(Attempt.HoldBeforeJoin());
        var phase = 0;
        await fx.RunUntil(() =>
        {
            if (fx.Script.Latest is null)
                return false;
            if (phase == 0 && fx.Script.Sent.Count == 0)
            {
                fx.Script.Latest.Push(DeployedHello);
                phase = 1;
                return false;
            }

            if (phase == 1 && fx.Script.Sent.Count >= 1)
            {
                fx.Script.Latest.Push(Welcome);
                phase = 2;
                return false;
            }

            if (phase == 2 && fx.Script.Sent.Any(IsType("pull")))
            {
                fx.Script.Latest.Push("""{"type":"delivery","deliveryId":"d1","leaseGeneration":1,"nick":"alex","text":"hello from fixture"}""");
                phase = 3;
                return false;
            }

            if (phase != 3)
                return false;
            var inboundPath = Path.Combine(fx.Dir.Path, V2Client.InboundName);
            if (!File.Exists(inboundPath))
                return false;
            var inbound = File.ReadAllText(inboundPath);
            if (!inbound.Contains("\"op\":\"seen\"", StringComparison.Ordinal))
                return false;
            Assert.DoesNotContain("\"op\":\"ack_pending\"", inbound, StringComparison.Ordinal);
            Assert.DoesNotContain("\"op\":\"handed_off\"", inbound, StringComparison.Ordinal);
            Assert.DoesNotContain(fx.Script.Sent, line => IsType("ack")(line));
            var room = File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl"));
            Assert.Contains("not acked", room, StringComparison.Ordinal);
            Assert.DoesNotContain("v2_handoff", room, StringComparison.Ordinal);
            return true;
        });
    }

    private static JsonObject Delivery(int generation, string id = "<delivery-id>", string text = "hello from fixture")
    {
        var frame = Fixture("deployed", "delivery");
        frame["deliveryId"] = id;
        frame["leaseGeneration"] = generation;
        frame["text"] = text;
        frame["nick"] = "alex";
        return frame;
    }

    private static JsonObject Ack(string deliveryId, int generation) => new()
    {
        ["type"] = "ack_result",
        ["state"] = "processed",
        ["deliveryId"] = deliveryId,
        ["leaseGeneration"] = generation
    };

    private static V2Client Ready(string dir, IV2ConsumerQueue? queue = null)
    {
        var client = V2Client.Open(dir);
        client.Consumers = queue ?? new AcceptQueue();
        return client;
    }

    private sealed class AcceptQueue : IV2ConsumerQueue
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public string? LastText { get; private set; }
        public Action? OnEnqueue { get; set; }

        public bool TryEnqueue(V2QueuedDelivery delivery, out string? error)
        {
            OnEnqueue?.Invoke();
            Calls++;
            LastText = delivery.Text;
            if (Fail)
            {
                error = "IOException";
                return false;
            }

            error = null;
            return true;
        }
    }

    private static JsonObject Fixture(string group, string id)
    {
        var path = Path.Combine(RepoRoot(), "tests", "Chief.Bridge.Tests", "Fixtures", "v2", "frames.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var node in root[group]!.AsArray())
        {
            var row = node!.AsObject();
            if (Json.Str(row, "id") == id)
                return (JsonObject)row["frame"]!.DeepClone();
        }

        throw new InvalidOperationException($"missing fixture {group}/{id}");
    }

    private static void AssertSameShape(JsonObject expected, JsonObject actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var pair in expected)
        {
            Assert.True(actual.ContainsKey(pair.Key));
            Assert.Equal(pair.Value!.ToJsonString(), actual[pair.Key]!.ToJsonString());
        }
    }

    private static Func<string, bool> IsType(string type) =>
        json =>
        {
            try
            {
                return Json.Str(Parse(json), "type") == type;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        };

    private static bool IsJoin(string json) => Json.Str(Parse(json), "type") == "join";

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static long Num(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i))
                return i;
            if (value.TryGetValue<long>(out var l))
                return l;
        }

        throw new InvalidOperationException("expected a number");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "chatbridge-v2-contract.md")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
