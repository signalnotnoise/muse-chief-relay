using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

public class HivemindMirrorTests
{
    private const string Uuid = "11111111-1111-4111-8111-111111111111";
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Workspace_key_matches_sha256_and_is_not_the_channel()
    {
        Assert.Equal(AbcHash, MirrorPayload.WorkspaceKey("abc"));
        Assert.Equal(AbcHash, MirrorPayload.WorkspaceKey("  abc  "));
        Assert.NotEqual("throwaway-test", MirrorPayload.WorkspaceKey("throwaway-test"));
        Assert.Equal(64, MirrorPayload.WorkspaceKey("throwaway-test").Length);
        Assert.Equal("", MirrorPayload.WorkspaceKey("   "));
    }

    [Fact]
    public void Payload_keeps_the_uuid_and_drops_channel_trip_and_secrets()
    {
        var chat = JsonNode.Parse("""
            {
              "cmd": "chat",
              "nick": "Ada",
              "text": "hello",
              "id": "11111111-1111-4111-8111-111111111111",
              "time": 1700000000,
              "channel": "throwaway-test",
              "room": "throwaway-test",
              "trip": "AbCdEf",
              "password": "join-secret",
              "pass": "nope",
              "token": "session-token"
            }
            """)!.AsObject();

        var built = MirrorPayload.Build(chat, "throwaway-test", 99);
        Assert.True(built.Ok);
        Assert.DoesNotContain("throwaway-test", built.Json);
        Assert.DoesNotContain("AbCdEf", built.Json);
        Assert.DoesNotContain("join-secret", built.Json);
        Assert.DoesNotContain("nope", built.Json);
        Assert.DoesNotContain("session-token", built.Json);
        Assert.Contains(Uuid, built.Json);
        Assert.Contains(MirrorPayload.WorkspaceKey("throwaway-test"), built.Json);
        Assert.Contains("\"sender\":\"Ada\"", built.Json);
        Assert.Contains("\"threadKey\":\"room\"", built.Json);
        Assert.Contains("\"ts\":1700000000", built.Json);
        Assert.DoesNotContain("\"name\"", built.Json);
    }

    [Fact]
    public void Oversize_text_and_a_non_uuid_are_refused()
    {
        var missing = JsonNode.Parse("""{"cmd":"chat","nick":"Ada","text":"hello"}""")!.AsObject();
        Assert.Equal("id", MirrorPayload.Build(missing, "abc", 10).Reason);

        var huge = new JsonObject
        {
            ["nick"] = "Ada",
            ["text"] = new string('x', 8193),
            ["id"] = Uuid
        };
        Assert.Equal("length", MirrorPayload.Build(huge, "abc", 10).Reason);
    }

    [Fact]
    public void Flag_off_does_not_open_a_mirror()
    {
        var calls = 0;
        Assert.Null(HivemindMirror.Open(new BridgeRuntime
        {
            Env = _ => "0",
            MirrorOffer = _ => calls++
        }, _ => { }));
        Assert.Null(HivemindMirror.Open(new BridgeRuntime
        {
            Env = _ => null,
            MirrorOffer = _ => calls++
        }, _ => { }));
        Assert.Null(HivemindMirror.Open(new BridgeRuntime
        {
            Env = _ => "true",
            MirrorOffer = _ => calls++
        }, _ => { }));
        Assert.Equal(0, calls);

        var opened = HivemindMirror.Open(new BridgeRuntime
        {
            Env = key => key == "HIVEMIND_MESSAGE_MIRROR" ? "1" : null,
            MirrorOffer = _ => calls++
        }, _ => { });
        Assert.NotNull(opened);
    }

    [Fact]
    public async Task Accepted_chat_is_offered_only_when_the_flag_is_on()
    {
        var offers = new List<string>();
        var gate = new object();
        await using (var off = new RelayFixture
        {
            Env = _ => "0",
            MirrorOffer = line => { lock (gate) offers.Add(line); }
        })
        {
            await DriveChat(off, "kept-locally", () => InboxHas(off, "kept-locally"));
            lock (gate) Assert.Empty(offers);
        }

        await using var on = new RelayFixture
        {
            Env = key => key == "HIVEMIND_MESSAGE_MIRROR" ? "1" : null,
            MirrorOffer = line => { lock (gate) offers.Add(line); }
        };
        await DriveChat(on, "hello", () => { lock (gate) return offers.Count == 1; });
        string offer;
        lock (gate) offer = Assert.Single(offers);
        Assert.Contains(Uuid, offer);
        Assert.Contains(MirrorPayload.WorkspaceKey("throwaway-test"), offer);
        Assert.DoesNotContain("throwaway-test", offer);
        Assert.DoesNotContain("AbCdEf", offer);
        Assert.DoesNotContain("join-secret", offer);
    }

    [Fact]
    public void Nonzero_helper_without_a_safe_line_is_logged()
    {
        var crash = new List<string>();
        NodeHivemindMirror.LogHelperResult("SyntaxError: unexpected token\n    at startup\n", 1, crash.Add);
        Assert.Equal("hivemind message mirror failed (exit)", Assert.Single(crash));
        Assert.DoesNotContain("SyntaxError", crash[0]);

        var empty = new List<string>();
        NodeHivemindMirror.LogHelperResult("", 2, empty.Add);
        Assert.Equal("hivemind message mirror failed (exit)", Assert.Single(empty));

        var forwarded = new List<string>();
        NodeHivemindMirror.LogHelperResult("hivemind message mirror failed (error)\n", 1, forwarded.Add);
        Assert.Equal("hivemind message mirror failed (error)", Assert.Single(forwarded));

        var quiet = new List<string>();
        NodeHivemindMirror.LogHelperResult("SyntaxError: unexpected token\n", 0, quiet.Add);
        Assert.Empty(quiet);
    }

    [Fact]
    public async Task A_throwing_mirror_does_not_drop_the_chat()
    {
        await using var fx = new RelayFixture
        {
            Env = key => key == "HIVEMIND_MESSAGE_MIRROR" ? "1" : null,
            MirrorOffer = _ => throw new InvalidOperationException("secret-marker")
        };
        await DriveChat(fx, "still-delivered", () => InboxHas(fx, "still-delivered"));
        var inbox = File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl"));
        Assert.Contains("still-delivered", inbox);
        Assert.DoesNotContain("secret-marker", inbox);
    }

    private static async Task DriveChat(RelayFixture fx, string text, Func<bool> done)
    {
        var sent = 0;
        fx.Script.Enqueue(Attempt.OnlineSetHold(fx.Nick));
        await fx.RunUntil(() =>
        {
            if (sent == 0 && fx.Script.Latest is not null && Connected(fx))
            {
                sent = 1;
                fx.Script.Latest.Push(ChatFrame(text));
            }

            return sent == 1 && done();
        });
    }

    private static string ChatFrame(string text) =>
        $$"""
        {"cmd":"chat","nick":"Ada","trip":"AbCdEf","password":"join-secret","text":"{{text}}","id":"{{Uuid}}","time":1700000000,"channel":"throwaway-test"}
        """;

    private static bool Connected(RelayFixture fx)
    {
        try
        {
            var path = Path.Combine(fx.Dir.Path, "state.json");
            if (!File.Exists(path))
                return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("connected", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool InboxHas(RelayFixture fx, string needle)
    {
        var path = Path.Combine(fx.Dir.Path, "inbox.jsonl");
        return File.Exists(path) && File.ReadAllText(path).Contains(needle, StringComparison.Ordinal);
    }
}
