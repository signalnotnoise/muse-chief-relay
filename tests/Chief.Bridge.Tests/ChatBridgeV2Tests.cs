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
        var client = V2Client.Open(dir.Path);
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

        var delivery = client.OnFrame(Fixture("deployed", "delivery"));
        var inbound = File.ReadAllText(client.InboundPath);
        var seenAt = inbound.IndexOf("\"op\":\"seen\"", StringComparison.Ordinal);
        var pendingAt = inbound.IndexOf("\"op\":\"ack_pending\"", StringComparison.Ordinal);
        Assert.True(seenAt >= 0 && pendingAt > seenAt);
        Assert.Equal("ack", delivery.Send.Single()["type"]!.GetValue<string>());
        Assert.Equal(1, Num(delivery.Send.Single()["leaseGeneration"]));
    }

    [Fact]
    public void Processed_generation_is_deduped_and_seen_or_fenced_can_be_released()
    {
        using var dir = new TempDir();
        var client = V2Client.Open(dir.Path);

        var first = client.OnFrame(Delivery(1));
        Assert.Single(first.Send);
        var again = client.OnFrame(Delivery(1));
        Assert.Empty(again.Send);
        Assert.DoesNotContain("\"op\":\"processed\"", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);

        var done = client.OnFrame(Fixture("deployed", "ack_result_processed"));
        Assert.Contains("\"how\":\"processed\"", File.ReadAllText(client.InboundPath), StringComparison.Ordinal);
        Assert.Empty(client.OnFrame(Delivery(1)).Send);

        var next = client.OnFrame(Delivery(2));
        Assert.Equal(2, Num(next.Send.Single()["leaseGeneration"]));

        using var fencedDir = new TempDir();
        var fenced = V2Client.Open(fencedDir.Path);
        Assert.Single(fenced.OnFrame(Delivery(1)).Send);
        var fencedResult = fenced.OnFrame(Fixture("deployed", "lease_fenced"));
        Assert.Empty(fencedResult.Send);
        Assert.DoesNotContain("\"op\":\"processed\"", File.ReadAllText(fenced.InboundPath), StringComparison.Ordinal);
        var redelivered = fenced.OnFrame(Delivery(1));
        Assert.Equal(1, Num(redelivered.Send.Single()["leaseGeneration"]));

        using var idemDir = new TempDir();
        var idem = V2Client.Open(idemDir.Path);
        Assert.Single(idem.OnFrame(Delivery(4)).Send);
        idem.OnFrame(Fixture("deployed", "ack_result_idempotent"));
        Assert.Contains("\"how\":\"idempotent\"", File.ReadAllText(idem.InboundPath), StringComparison.Ordinal);
        Assert.Empty(idem.OnFrame(Delivery(4)).Send);
    }

    [Fact]
    public void Second_delivery_waits_until_the_in_flight_ack_settles()
    {
        using var dir = new TempDir();
        var client = V2Client.Open(dir.Path);
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
        var client = V2Client.Open(dir.Path);
        client.BeginDeployed(outbox);
        File.AppendAllText(outbox, """{"text":"keep me"}""" + "\n");
        var sent = client.NextChat();
        Assert.True(sent.Send);
        Assert.Single(client.OnFrame(Delivery(3, "keep")).Send);

        var reopened = V2Client.Open(dir.Path);
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
        Assert.False(client.NextChat().Send);

        File.AppendAllText(outbox, """{"text":"in flight"}""" + "\n");
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
                fx.Script.Latest.Push("""{"type":"delivery","deliveryId":"d1","leaseGeneration":1,"trip":"Zz99Yy"}""");
                phase = 3;
                return false;
            }

            if (phase == 3 && fx.Script.Sent.Any(IsType("ack")))
            {
                var inbound = File.ReadAllText(Path.Combine(fx.Dir.Path, V2Client.InboundName));
                Assert.True(inbound.IndexOf("\"op\":\"seen\"", StringComparison.Ordinal) <
                            inbound.IndexOf("\"op\":\"ack_pending\"", StringComparison.Ordinal));
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
            Env = new Dictionary<string, string> { ["CHATBRIDGE_INBOX_OWNER"] = owner }
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

    private static JsonObject Delivery(int generation, string id = "<delivery-id>")
    {
        var frame = Fixture("deployed", "delivery");
        frame["deliveryId"] = id;
        frame["leaseGeneration"] = generation;
        return frame;
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
