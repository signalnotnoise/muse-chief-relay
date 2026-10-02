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
    public void Voizle_millisecond_ts_is_stored_as_epoch_seconds()
    {
        var built = MirrorPayload.Build(Chat("ts", 1_790_970_720_123L), "throwaway-test", 99);
        Assert.True(built.Ok, built.Reason);
        Assert.Equal(1_790_970_720L, PayloadTs(built.Json));

        var fromWire = JsonNode.Parse("""
            {"cmd":"chat","nick":"Ada","text":"hello","id":"11111111-1111-4111-8111-111111111111","ts":1790970720123,"room":"throwaway-test"}
            """)!.AsObject();
        var parsed = MirrorPayload.Build(fromWire, "throwaway-test", 99);
        Assert.True(parsed.Ok, parsed.Reason);
        Assert.Equal(1_790_970_720L, PayloadTs(parsed.Json));

        var even = MirrorPayload.Build(Chat("ts", 1_720_000_000_000L), "throwaway-test", 99);
        Assert.True(even.Ok, even.Reason);
        Assert.Equal(1_720_000_000L, PayloadTs(even.Json));

        var seconds = MirrorPayload.Build(Chat("time", 1_700_000_000L), "throwaway-test", 99);
        Assert.True(seconds.Ok, seconds.Reason);
        Assert.Equal(1_700_000_000L, PayloadTs(seconds.Json));

        var timeWins = Chat("time", 1_700_000_000L);
        timeWins["ts"] = 1_790_970_720_123L;
        var preferred = MirrorPayload.Build(timeWins, "throwaway-test", 99);
        Assert.True(preferred.Ok, preferred.Reason);
        Assert.Equal(1_700_000_000L, PayloadTs(preferred.Json));

        var ceiling = MirrorPayload.Build(Chat("ts", MirrorPayload.TsMax), "throwaway-test", 99);
        Assert.True(ceiling.Ok, ceiling.Reason);
        Assert.Equal(MirrorPayload.TsMax, PayloadTs(ceiling.Json));

        var msCeiling = MirrorPayload.Build(Chat("ts", MirrorPayload.TsMaxMillis), "throwaway-test", 99);
        Assert.True(msCeiling.Ok, msCeiling.Reason);
        Assert.Equal(MirrorPayload.TsMax, PayloadTs(msCeiling.Json));

        Assert.Equal("type", MirrorPayload.Build(Chat("ts", MirrorPayload.TsMaxMillis + 1), "throwaway-test", 99).Reason);
        Assert.Equal("type", MirrorPayload.Build(Chat("ts", -1), "throwaway-test", 99).Reason);
        Assert.Equal("type", MirrorPayload.Build(Chat("time", MirrorPayload.TsMaxMillis + 1), "abc", 99).Reason);

        var missing = Chat("time", 1);
        missing.Remove("time");
        var fallback = MirrorPayload.Build(missing, "throwaway-test", 99);
        Assert.True(fallback.Ok, fallback.Reason);
        Assert.Equal(99L, PayloadTs(fallback.Json));
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

    [Fact(Timeout = 15000)]
    public void Burst_larger_than_the_queue_cap_is_mirrored_from_the_spill_file()
    {
        using var dir = new TempDir();
        var spill = dir.File(NodeHivemindMirror.SpillFileName);
        var logs = new LockedLines();
        var seen = new LockedLines();
        var total = NodeHivemindMirror.MaxQueued + 9;
        var mirror = NodeHivemindMirror.StartForTest(logs.Add, spill, seen.Add, start: false);

        for (var n = 1; n <= total; n++)
            mirror.Offer(RoomChat(Id(n), "burst-text-marker"), "throwaway-test");

        var spilled = File.ReadAllText(spill);
        var spillLines = spilled.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(9, spillLines.Length);
        Assert.Contains(Id(NodeHivemindMirror.MaxQueued + 1), spilled);
        Assert.Contains("burst-text-marker", spilled);
        Assert.DoesNotContain("throwaway-test", spilled);
        Assert.DoesNotContain(Id(1), spilled);
        Assert.Equal(9, logs.Count("hivemind message mirror deferred (queue)"));
        Assert.Equal(0, logs.Count("hivemind message mirror refused (queue)"));
        Assert.DoesNotContain("burst-text-marker", logs.Text());
        Assert.DoesNotContain("throwaway-test", logs.Text());

        mirror.Start();
        Assert.True(WaitUntil(() => seen.Count() == total && !File.Exists(spill)));
        for (var n = 1; n <= total; n++)
            Assert.Contains(Id(n), seen.Text());
        Assert.Equal(0, logs.Count("hivemind message mirror refused (queue)"));
        Assert.DoesNotContain("burst-text-marker", logs.Text());
    }

    [Fact(Timeout = 15000)]
    public void Burst_while_the_helper_is_busy_still_mirrors_past_the_old_cap()
    {
        using var dir = new TempDir();
        var spill = dir.File(NodeHivemindMirror.SpillFileName);
        var logs = new LockedLines();
        var seen = new LockedLines();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var running = 0;
        var overflow = 9;
        var total = 1 + NodeHivemindMirror.MaxQueued + overflow;
        var mirror = NodeHivemindMirror.StartForTest(logs.Add, spill, json =>
        {
            seen.Add(json);
            if (Interlocked.Increment(ref running) == 1)
            {
                entered.Set();
                release.Wait();
            }
        });

        try
        {
            mirror.Offer(RoomChat(Id(1), "burst-text-marker"), "throwaway-test");
            Assert.True(entered.Wait(5000));
            for (var n = 2; n <= total; n++)
                mirror.Offer(RoomChat(Id(n), "burst-text-marker"), "throwaway-test");

            var spilled = File.ReadAllText(spill);
            Assert.Equal(overflow, spilled.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal(overflow, logs.Count("hivemind message mirror deferred (queue)"));
            Assert.Equal(0, logs.Count("hivemind message mirror refused (queue)"));
            Assert.DoesNotContain("burst-text-marker", logs.Text());
            Assert.DoesNotContain("throwaway-test", logs.Text());

            release.Set();
            Assert.True(WaitUntil(() => seen.Count() == total && !File.Exists(spill)));
            for (var n = 1; n <= total; n++)
                Assert.Contains(Id(n), seen.Text());
            Assert.DoesNotContain("refused (type)", logs.Text());
        }
        finally
        {
            release.Set();
        }
    }

    [Fact(Timeout = 15000)]
    public void A_spill_that_cannot_be_written_is_refused_without_throwing()
    {
        using var dir = new TempDir();
        var blocker = dir.File("not-a-directory");
        File.WriteAllText(blocker, "x");
        var spill = Path.Combine(blocker, NodeHivemindMirror.SpillFileName);
        var logs = new LockedLines();
        var seen = new LockedLines();
        var extra = 3;
        var mirror = NodeHivemindMirror.StartForTest(logs.Add, spill, seen.Add, start: false);

        for (var n = 1; n <= NodeHivemindMirror.MaxQueued + extra; n++)
            mirror.Offer(RoomChat(Id(n), "burst-text-marker"), "throwaway-test");

        Assert.Equal(extra, logs.Count("hivemind message mirror refused (queue)"));
        Assert.Equal(0, logs.Count("hivemind message mirror deferred (queue)"));
        Assert.DoesNotContain("burst-text-marker", logs.Text());
        Assert.DoesNotContain("throwaway-test", logs.Text());
        Assert.False(File.Exists(spill));

        mirror.Start();
        Assert.True(WaitUntil(() => seen.Count() == NodeHivemindMirror.MaxQueued));
        Assert.Contains(Id(1), seen.Text());
        Assert.Contains(Id(NodeHivemindMirror.MaxQueued), seen.Text());
        Assert.DoesNotContain(Id(NodeHivemindMirror.MaxQueued + 1), seen.Text());
    }

    [Fact(Timeout = 15000)]
    public void Spill_file_from_a_previous_process_is_drained_and_a_bad_line_is_skipped()
    {
        using var dir = new TempDir();
        var spill = dir.File(NodeHivemindMirror.SpillFileName);
        var first = MirrorPayload.Build(RoomChat(Id(1), "burst-text-marker"), "throwaway-test", 1700000000);
        var second = MirrorPayload.Build(RoomChat(Id(2), "burst-text-marker"), "throwaway-test", 1700000000);
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        File.WriteAllText(spill, "corrupt-spill-line\n" + first.Json + "\n" + second.Json + "\n");

        var logs = new LockedLines();
        var seen = new LockedLines();
        _ = NodeHivemindMirror.StartForTest(logs.Add, spill, seen.Add);

        Assert.True(WaitUntil(() => seen.Count() == 2 && !File.Exists(spill)));
        Assert.Contains(Id(1), seen.Text());
        Assert.Contains(Id(2), seen.Text());
        Assert.Equal(1, logs.Count("hivemind message mirror refused (json)"));
        Assert.DoesNotContain("corrupt-spill-line", logs.Text());
        Assert.DoesNotContain("burst-text-marker", logs.Text());
        Assert.DoesNotContain("throwaway-test", logs.Text());
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

    private static long PayloadTs(string json) =>
        JsonNode.Parse(json)!["ts"]!.GetValue<long>();

    private static JsonObject Chat(string stampKey, long stamp)
    {
        var chat = JsonNode.Parse($$"""
            {"cmd":"chat","nick":"Ada","text":"hello","id":"{{Uuid}}","room":"throwaway-test"}
            """)!.AsObject();
        chat[stampKey] = stamp;
        return chat;
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

    private static string Id(int n) => $"00000000-0000-4000-8000-{n:x12}";

    private static JsonObject RoomChat(string id, string text) =>
        JsonNode.Parse($$"""
            {"cmd":"chat","nick":"Ada","text":"{{text}}","id":"{{id}}","time":1700000000,"channel":"throwaway-test"}
            """)!.AsObject();

    private static bool WaitUntil(Func<bool> done, int ms = 5000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (done())
                return true;
            Thread.Sleep(10);
        }

        return done();
    }

    private sealed class LockedLines
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = new();

        public void Add(string line)
        {
            lock (_gate)
                _lines.Add(line);
        }

        public int Count()
        {
            lock (_gate)
                return _lines.Count;
        }

        public int Count(string exact)
        {
            lock (_gate)
                return _lines.Count(line => line == exact);
        }

        public string Text()
        {
            lock (_gate)
                return string.Join("\n", _lines);
        }
    }
}
