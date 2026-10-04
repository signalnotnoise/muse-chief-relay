using System.Text;
using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

public class VoizleRelayTests
{
    private const string Hello = """{"v":1,"type":"hello","protocol":"voizle-text-relay"}""";

    private const string Welcome = """
        {"v":1,"type":"welcome","sessionId":"s1","room":"throwaway-test","nick":"n","trip":"!Ab12Cd","users":[{"sessionId":"s1","nick":"n","trip":"!Ab12Cd"}],"replay":[{"v":1,"type":"chat","room":"throwaway-test","nick":"Alex","trip":"!Ab12Cd","text":"from replay"}]}
        """;

    [Fact]
    public void Auto_ack_and_outbox_chat_leave_as_v1_frames()
    {
        var ack = VoizleWire.FromOutbox(new JsonObject { ["cmd"] = "chat", ["text"] = "(auto) got it" });
        Assert.Equal("chat", ack["type"]!.GetValue<string>());
        Assert.Equal("(auto) got it", ack["text"]!.GetValue<string>());
        Assert.False(ack.ContainsKey("cmd"));

        var chat = VoizleWire.FromOutbox(new JsonObject { ["text"] = "hello room" });
        Assert.Equal(1, chat["v"]!.GetValue<int>());
        Assert.False(chat.ContainsKey("cmd"));
    }

    [Fact]
    public void Public_trip_is_the_bang_form_and_a_password_is_not()
    {
        Assert.Equal("!Ab12Cd", PublicTrip.ForJoin("Ab12Cd"));
        Assert.Equal("!Ab12Cd", PublicTrip.ForJoin("  !Ab12Cd "));
        Assert.Null(PublicTrip.ForJoin(""));
        Assert.Null(PublicTrip.ForJoin("hunter2"));
        Assert.Null(PublicTrip.ForJoin("name#secret"));
        Assert.Equal("Ab12Cd", PublicTrip.Canonical("!Ab12Cd"));
        Assert.Null(PublicTrip.Canonical("!"));
    }

    [Fact]
    public void Hack_chat_urls_keep_the_old_handshake()
    {
        Assert.False(RelayUrl.SpeaksVoizle("wss://hack.chat/chat-ws"));
        Assert.False(RelayUrl.SpeaksVoizle("wss://www.hack.chat/chat-ws"));
        Assert.True(RelayUrl.SpeaksVoizle("ws://127.0.0.1:8787/relay"));
        Assert.True(RelayUrl.SpeaksVoizle("wss://relay.example.com/relay"));
    }

    [Fact]
    public void A_password_in_trip_is_a_bad_config()
    {
        using var dir = new TempDir();
        var path = dir.File("c.json");
        File.WriteAllText(path, """{"channel":"c","nick":"n","url":"ws://127.0.0.1:8787/relay","trip":"hunter2"}""");
        var ex = Assert.Throws<ConfigException>(() => RelayConfig.Load(path, false, dir.Path, _ => null));
        Assert.Contains("trip must be a public code", ex.Message);
    }

    [Fact]
    public void Type_only_chat_is_a_watched_chat_with_a_canonical_trip()
    {
        var frame = InboundFrame.Parse("""{"v":1,"type":"chat","room":"lobby","nick":"Alex","trip":"!Ab12Cd","text":"hey chief"}""");
        Assert.Equal("chat", frame.Cmd);
        Assert.Equal("chat", frame.Object!["cmd"]!.GetValue<string>());
        Assert.Equal("lobby", frame.Object["channel"]!.GetValue<string>());
        Assert.Equal("Ab12Cd", frame.Object["trip"]!.GetValue<string>());

        var row = new JsonObject
        {
            ["ts"] = 1,
            ["dir"] = "in",
            ["msg"] = frame.LogNode.DeepClone()
        };
        var watched = InboxWatcher.ParseLine(Encoding.UTF8.GetBytes(row.ToJsonString()), "chief");
        Assert.NotNull(watched);
        Assert.Equal("Alex", watched!.Nick);
        Assert.Equal("Ab12Cd", watched.Trip);
        Assert.Equal("hey chief", watched.Text);

        // A raw v1 line that never went through the bridge normalizer is still watched.
        var raw = """{"ts":2,"dir":"in","msg":{"v":1,"type":"chat","nick":"Alex","trip":"!Ab12Cd","text":"later"}}""";
        var again = InboxWatcher.ParseLine(Encoding.UTF8.GetBytes(raw), "chief");
        Assert.Equal("Ab12Cd", again!.Trip);
        Assert.Equal("later", again.Text);
    }

    [Fact]
    public async Task Voizle_waits_for_hello_then_speaks_v1()
    {
        await using var fx = new RelayFixture
        {
            Url = "ws://127.0.0.1:8787/relay",
            Trip = "Ab12Cd"
        };
        fx.Script.Enqueue(Attempt.HoldBeforeJoin());
        var phase = 0;
        DateTime? opened = null;
        string? inbox = null;

        await fx.RunUntil(() =>
        {
            if (fx.Script.Created == 0 || fx.Script.Latest is null)
                return false;
            opened ??= DateTime.UtcNow;
            inbox ??= Path.Combine(fx.Dir.Path, "inbox.jsonl");

            if (phase == 0)
            {
                if ((DateTime.UtcNow - opened.Value).TotalMilliseconds < 200)
                    return false;
                Assert.Empty(fx.Script.Sent);
                fx.Script.Latest.Push(Hello);
                phase = 1;
                return false;
            }

            if (phase == 1 && fx.Script.Sent.Count >= 1)
            {
                var join = JsonNode.Parse(fx.Script.Sent[0])!.AsObject();
                Assert.Equal(1, join["v"]!.GetValue<int>());
                Assert.Equal("join", join["type"]!.GetValue<string>());
                Assert.Equal("throwaway-test", join["room"]!.GetValue<string>());
                Assert.Equal(fx.Nick, join["nick"]!.GetValue<string>());
                Assert.Equal("!Ab12Cd", join["trip"]!.GetValue<string>());
                Assert.False(join.ContainsKey("cmd"));
                Assert.False(join.ContainsKey("channel"));
                Assert.False(join.ContainsKey("pass"));
                fx.Script.Latest.Push(Welcome);
                phase = 2;
                return false;
            }

            if (phase == 2 && File.Exists(Path.Combine(fx.Dir.Path, "state.json")))
            {
                var state = File.ReadAllText(Path.Combine(fx.Dir.Path, "state.json"));
                if (!state.Contains("\"connected\":true", StringComparison.Ordinal))
                    return false;
                var logged = File.ReadAllText(inbox);
                Assert.Contains("from replay", logged);
                var replay = WatchedLines(logged).Single(c => c.Text == "from replay");
                Assert.Equal("Alex", replay.Nick);
                Assert.Equal("Ab12Cd", replay.Trip);
                File.AppendAllText(
                    Path.Combine(fx.Dir.Path, "outbox.jsonl"),
                    """{"cmd":"chat","text":"hello room"}""" + "\n");
                fx.Script.Latest.Push("""{"v":1,"type":"chat","room":"throwaway-test","nick":"Alex","trip":"!Xy34Zw","text":"live line"}""");
                phase = 3;
                return false;
            }

            if (phase != 3)
                return false;
            var chat = fx.Script.Sent.Select(ParseObject).FirstOrDefault(o => Json.Str(o, "type") == "chat");
            if (chat is null)
                return false;
            Assert.Equal("hello room", chat["text"]!.GetValue<string>());
            Assert.Equal(1, chat["v"]!.GetValue<int>());
            Assert.False(chat.ContainsKey("cmd"));
            var live = WatchedLines(File.ReadAllText(inbox)).Single(c => c.Text == "live line");
            Assert.Equal("Xy34Zw", live.Trip);
            return true;
        });
    }

    [Fact]
    public async Task Voizle_error_before_welcome_rejects_the_join()
    {
        await using var fx = new RelayFixture { Url = "ws://127.0.0.1:8787/relay" };
        fx.Script.Enqueue(Attempt.HoldBeforeJoin(), Attempt.HoldBeforeJoin());
        var phase = 0;

        await fx.RunUntil(() =>
        {
            if (fx.Script.Created < 1 || fx.Script.Latest is null)
                return false;
            if (phase == 0 && fx.Script.Created == 1 && fx.Script.Sent.Count == 0)
            {
                fx.Script.Latest.Push(Hello);
                phase = 1;
                return false;
            }

            if (phase == 1 && fx.Script.Sent.Count >= 1)
            {
                fx.Script.Latest.Push("""{"v":1,"type":"error","code":"nick_taken","text":"nick is taken"}""");
                phase = 2;
                return false;
            }

            var inbox = Path.Combine(fx.Dir.Path, "inbox.jsonl");
            return phase == 2 && File.Exists(inbox) && File.ReadAllText(inbox).Contains("join rejected: nick is taken");
        });
    }

    [Fact]
    public async Task Welcome_replay_is_logged_but_not_auto_acked()
    {
        await using var fx = new RelayFixture
        {
            Url = "ws://127.0.0.1:8787/relay",
            Trip = "Ab12Cd",
            AutoAck = true,
            MentionTrips = ["Xy34Zw"]
        };
        fx.Script.Enqueue(Attempt.HoldBeforeJoin());
        var phase = 0;
        DateTime? connectedAt = null;

        await fx.RunUntil(() =>
        {
            if (fx.Script.Created == 0 || fx.Script.Latest is null)
                return false;

            if (phase == 0 && fx.Script.Sent.Count == 0)
            {
                fx.Script.Latest.Push(Hello);
                phase = 1;
                return false;
            }

            if (phase == 1 && fx.Script.Sent.Count >= 1)
            {
                fx.Script.Latest.Push("""
                    {"v":1,"type":"welcome","sessionId":"s1","room":"throwaway-test","nick":"n","trip":"!Ab12Cd","replay":[{"v":1,"type":"chat","room":"throwaway-test","nick":"Alex","trip":"!Xy34Zw","text":"hey n"}]}
                    """);
                phase = 2;
                return false;
            }

            if (phase == 2)
            {
                var statePath = Path.Combine(fx.Dir.Path, "state.json");
                if (!File.Exists(statePath) || !File.ReadAllText(statePath).Contains("\"connected\":true", StringComparison.Ordinal))
                    return false;
                connectedAt ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - connectedAt.Value).TotalMilliseconds < 200)
                    return false;
                Assert.DoesNotContain(fx.Script.Sent, IsChatFrame);
                fx.Script.Latest.Push("""{"v":1,"type":"chat","room":"throwaway-test","nick":"Alex","trip":"!Xy34Zw","text":"hey n"}""");
                phase = 3;
                return false;
            }

            if (phase != 3)
                return false;
            var chat = fx.Script.Sent.Select(ParseObject).FirstOrDefault(o => Json.Str(o, "type") == "chat");
            if (chat is null)
                return false;
            Assert.Equal(1, chat["v"]!.GetValue<int>());
            Assert.Contains("got it", chat["text"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.False(chat.ContainsKey("cmd"));
            return true;
        });
    }

    [Fact]
    public async Task Welcome_without_a_trip_clears_a_trip_learned_earlier()
    {
        await using var fx = new RelayFixture
        {
            Url = "ws://127.0.0.1:8787/relay",
            Trip = "Ab12Cd",
            AutoAck = true,
            MentionTrips = ["Ab12Cd"]
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
                fx.Script.Latest.Push(Hello);
                phase = 1;
                return false;
            }

            if (phase == 1 && fx.Script.Sent.Count(IsJoinFrame) >= 1)
            {
                fx.Script.Latest.Push("""
                    {"v":1,"type":"welcome","sessionId":"s1","room":"throwaway-test","nick":"n","trip":"!Ab12Cd"}
                    """);
                phase = 2;
                return false;
            }

            if (phase == 2)
            {
                var statePath = Path.Combine(fx.Dir.Path, "state.json");
                if (!File.Exists(statePath) || !File.ReadAllText(statePath).Contains("\"connected\":true", StringComparison.Ordinal))
                    return false;
                fx.Script.Latest.Close();
                phase = 3;
                return false;
            }

            if (phase == 3 && fx.Script.Created >= 2)
            {
                if (!secondHello)
                {
                    fx.Script.Latest.Push(Hello);
                    secondHello = true;
                    return false;
                }

                if (fx.Script.Sent.Count(IsJoinFrame) < 2)
                    return false;
                fx.Script.Latest.Push("""
                    {"v":1,"type":"welcome","sessionId":"s2","room":"throwaway-test","nick":"n"}
                    """);
                phase = 4;
                return false;
            }

            if (phase == 4)
            {
                var inbox = Path.Combine(fx.Dir.Path, "inbox.jsonl");
                if (!File.Exists(inbox))
                    return false;
                var welcomes = File.ReadAllText(inbox).Split('\n').Count(l => l.Contains("\"cmd\":\"welcome\"", StringComparison.Ordinal));
                if (welcomes < 2)
                    return false;
                fx.Script.Latest.Push("""{"v":1,"type":"chat","room":"throwaway-test","nick":"Alex","trip":"!Ab12Cd","text":"hey n"}""");
                phase = 5;
                return false;
            }

            if (phase != 5)
                return false;
            var chat = fx.Script.Sent.Select(ParseObject).FirstOrDefault(o => Json.Str(o, "type") == "chat");
            if (chat is null)
                return false;
            Assert.Contains("got it", chat["text"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.False(chat.ContainsKey("cmd"));
            return true;
        });
    }

    private static bool IsChatFrame(string json)
    {
        var o = ParseObject(json);
        return Json.Str(o, "type") == "chat" || Json.Str(o, "cmd") == "chat";
    }

    private static bool IsJoinFrame(string json)
    {
        var o = ParseObject(json);
        return Json.Str(o, "type") == "join" || Json.Str(o, "cmd") == "join";
    }

    private static JsonObject ParseObject(string json) => JsonNode.Parse(json)!.AsObject();

    private static IEnumerable<WatchedChat> WatchedLines(string inbox)
    {
        foreach (var line in inbox.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (InboxWatcher.ParseLine(Encoding.UTF8.GetBytes(line), "n") is { } chat)
                yield return chat;
        }
    }
}
