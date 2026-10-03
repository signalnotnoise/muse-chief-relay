using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

/// <summary>A throwaway HTTP endpoint on 127.0.0.1 that records every request. Never the real webhook.</summary>
internal sealed class LocalHook : IDisposable
{
    private readonly HttpListener _l = new();
    private readonly CancellationTokenSource _cts = new();
    public ConcurrentQueue<int> Responses { get; } = new();
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public ConcurrentQueue<(DateTime At, string? Auth, string? ContentType, string Body, int Status)> Requests { get; } = new();
    /// <summary>Return an HTTP status for this body, or null to use <see cref="Responses"/> / 200.</summary>
    public Func<string, int?>? StatusFor { get; set; }
    public string Url { get; }

    public LocalHook(string path = "hook/s3cr3t-path-9f2")
    {
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}/{path}";
        _l.Prefixes.Add($"http://127.0.0.1:{port}/");
        _l.Start();
        _ = Task.Run(LoopAsync);
    }

    public static int FreePort()
    {
        var t = new TcpListener(IPAddress.Loopback, 0);
        t.Start();
        var p = ((IPEndPoint)t.LocalEndpoint).Port;
        t.Stop();
        return p;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _l.GetContextAsync();
            }
            catch
            {
                return;
            }

            using var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await r.ReadToEndAsync();
            var status = StatusFor?.Invoke(body) ?? (Responses.TryDequeue(out var code) ? code : 200);
            Requests.Enqueue((DateTime.UtcNow, ctx.Request.Headers["Authorization"], ctx.Request.ContentType, body, status));
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay);
            ctx.Response.StatusCode = status;
            try
            {
                ctx.Response.Close();
            }
            catch
            {
                // client gave up (timeout test)
            }
        }
    }

    public JsonObject Last() => (JsonObject)JsonNode.Parse(Requests.Last().Body)!;

    public async Task<bool> WaitForAsync(int count, TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (Requests.Count < count && sw.Elapsed < within)
            await Task.Delay(20);
        return Requests.Count >= count;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _l.Close();
    }
}

public class HookConfigTests
{
    private static RelayConfig Load(string hookJson)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), $$"""{"channel":"c","nick":"chief"{{hookJson}}}""");
        return RelayConfig.Load(dir.File("config.json"), allowExampleFallback: false);
    }

    [Fact]
    public void Absent_block_means_not_configured()
    {
        var c = Load("");
        Assert.Null(c.Hook);
        Assert.Equal(HookState.NotConfigured, c.ReadHook(DateTimeOffset.UtcNow).State);
    }

    [Fact]
    public void Defaults_match_hookpoll()
    {
        var h = Load(""","hook":{}""").Hook!;
        Assert.Equal(("CHIEF_HOOK_URL", "CHIEF_HOOK_AUTH", "Bearer"), (h.UrlEnv, h.AuthEnv, h.AuthScheme));
        Assert.Equal((5.0, 15.0, 120.0, 15.0), (h.PollSeconds, h.CooldownSeconds, h.MaxRetrySeconds, h.TimeoutSeconds));
        Assert.Equal((2000, 50, ".hook.offset"), (h.MaxText, h.MaxBatch, h.State));
        Assert.Empty(h.Trips);
    }

    [Fact]
    public void Snake_case_fields_and_trip_normalisation()
    {
        var c = Load(""","hook":{"url_env":"U","auth_env":"","auth_scheme":"Token","poll_s":1,"cooldown_s":0,""" +
            """ "max_retry_s":30,"timeout_s":3,"trips":[" !Ab12Cd ","Ab12Cd",""],"state":"h/off","source":"s","max_text":10,"max_batch":2}""");
        var h = c.Hook!;
        Assert.Equal(("U", "", "Token"), (h.UrlEnv, h.AuthEnv, h.AuthScheme));
        Assert.Equal((1.0, 0.0, 30.0, 3.0), (h.PollSeconds, h.CooldownSeconds, h.MaxRetrySeconds, h.TimeoutSeconds));
        Assert.Equal(new[] { "Ab12Cd" }, h.Trips);
        Assert.Equal(Path.Combine(c.BaseDir, "h", "off.status"), c.HookStatusPath());
    }

    [Theory]
    [InlineData("""{"url_env":""}""")]
    [InlineData("""{"poll_s":0}""")]
    [InlineData("""{"poll_s":301}""")]
    [InlineData("""{"cooldown_s":-1}""")]
    [InlineData("""{"max_retry_s":0}""")]
    [InlineData("""{"timeout_s":0}""")]
    [InlineData("""{"max_text":0}""")]
    [InlineData("""{"max_batch":0}""")]
    [InlineData("""{"state":" "}""")]
    [InlineData("""{"auth_scheme":"Bearer x"}""")]
    public void Bad_values_are_config_errors(string block) =>
        Assert.Throws<ConfigException>(() => Load($""","hook":{block}"""));
}

public class HookSecretsTests
{
    private const string Url = "https://example.invalid/hooks/abc123secretpath";
    private const string Key = "k3y-DO-NOT-LEAK-7731";

    private static Func<string, string?> Env(params (string k, string v)[] kv) =>
        n => kv.FirstOrDefault(p => p.k == n).v;

    [Fact]
    public void Url_and_bearer_key_come_from_the_named_variables()
    {
        var s = HookSecrets.FromEnvironment(new HookConfig(), Env(("CHIEF_HOOK_URL", " " + Url + " "), ("CHIEF_HOOK_AUTH", Key + "\n")));
        Assert.Equal(Url, s.Url.OriginalString);
        Assert.Equal("Bearer " + Key, s.Authorization);
        Assert.DoesNotContain(Key, s.ToString());
        Assert.DoesNotContain("abc123", s.ToString());
    }

    [Fact]
    public void Empty_scheme_sends_the_bare_key_and_empty_auth_env_sends_none()
    {
        Assert.Equal(Key, HookSecrets.FromEnvironment(new HookConfig { AuthScheme = "" }, Env(("CHIEF_HOOK_URL", Url), ("CHIEF_HOOK_AUTH", Key))).Authorization);
        Assert.Null(HookSecrets.FromEnvironment(new HookConfig { AuthEnv = "" }, Env(("CHIEF_HOOK_URL", Url))).Authorization);
    }

    [Theory]
    [InlineData(null, Key)]                               // URL missing
    [InlineData(Url, null)]                               // key missing
    [InlineData("not a url " + Key, Key)]                 // not absolute
    [InlineData("ftp://example.invalid/abc123secretpath", Key)]
    [InlineData("http://example.invalid/abc123secretpath", Key)] // plain http off-box
    [InlineData(Url, "bad\u0001" + Key)]
    public void Errors_name_the_variable_never_the_value(string? url, string? key)
    {
        var ex = Assert.Throws<ConfigException>(() =>
            HookSecrets.FromEnvironment(new HookConfig(), n => n == "CHIEF_HOOK_URL" ? url : n == "CHIEF_HOOK_AUTH" ? key : null));
        Assert.Contains("CHIEF_HOOK_", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
        Assert.DoesNotContain("abc123", ex.Message);
        Assert.DoesNotContain("example.invalid", ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/x")]
    [InlineData("http://localhost:9/x")]
    [InlineData("http://[::1]:9/x")]
    public void Plain_http_is_allowed_to_loopback_only(string url) =>
        Assert.Equal(url, HookSecrets.FromEnvironment(new HookConfig { AuthEnv = "" }, Env(("CHIEF_HOOK_URL", url))).Url.OriginalString);

    [Fact]
    public void Hook_options_parse() 
    {
        Assert.False(HookOptions.Parse([]).Test);
        Assert.True(HookOptions.Parse(["--test"]).Test);
        Assert.Throws<ArgumentException>(() => HookOptions.Parse(["--bogus"]));
        Assert.Equal("hook", CliArgs.Parse(["hook", "--config", "/c.json", "--test"]).Command);
    }
}

public class HookPollerTests
{
    private const string Key = "k3y-DO-NOT-LEAK-7731";
    private const string Alex = "Ab12Cd";

    private static string Chat(string nick, string text, string? trip = null, long ts = 1790500000, string dir = "in")
    {
        var msg = new JsonObject { ["cmd"] = "chat", ["nick"] = nick, ["text"] = text, ["channel"] = "c" };
        if (trip is not null) msg["trip"] = trip;
        return new JsonObject { ["ts"] = ts, ["dir"] = dir, ["msg"] = msg }.ToJsonString(JsonUtil.Opts) + "\n";
    }

    private sealed class Rig : IDisposable
    {
        public TempDir Dir { get; } = new();
        public LocalHook Server { get; }
        public RelayConfig Cfg { get; }
        public HookSecrets Secrets { get; }
        public StringWriter Log { get; } = new();
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_500_000);
        public string Inbox => Dir.File("inbox.jsonl");
        private readonly List<HookPoller> _pollers = new();

        public Rig(Action<HookConfig>? tweak = null, string? url = null)
        {
            Server = new LocalHook();
            var h = new HookConfig { CooldownSeconds = 15, PollSeconds = 0.1, TimeoutSeconds = 5 };
            tweak?.Invoke(h);
            h.Validate("test");
            Cfg = new RelayConfig { Channel = "crtest-x", Nick = "chief", BaseDir = Dir.Path, ConfigPath = "test", Hook = h };
            var u = url ?? Server.Url;
            Secrets = HookSecrets.FromEnvironment(h, n => n == h.UrlEnv ? u : n == h.AuthEnv ? Key : null);
            File.WriteAllText(Inbox, Chat("Alex", "old history", Alex));
        }

        public HookPoller New()
        {
            var p = new HookPoller(Cfg, Secrets, clock: () => Now, log: Log);
            _pollers.Add(p);
            return p;
        }

        public void Add(string lines) => File.AppendAllText(Inbox, lines);

        public string AllOutput()
        {
            var parts = new List<string> { Log.ToString() };
            parts.AddRange(Directory.GetFiles(Dir.Path).Select(File.ReadAllText));
            return string.Join("\n", parts);
        }

        public void AssertNoSecrets()
        {
            var all = AllOutput();
            foreach (var s in Secrets.SecretValues())
                Assert.DoesNotContain(s, all);
            Assert.DoesNotContain(Key, all);
            Assert.DoesNotContain("s3cr3t-path", all);
        }

        public void Dispose()
        {
            foreach (var p in _pollers) p.Dispose();
            Server.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task First_run_starts_at_the_end_then_fires_new_chats_with_the_hookpoll_payload()
    {
        using var r = new Rig();
        var p = r.New();
        Assert.Equal(HookStep.Idle, await p.StepAsync(default)); // bootstrap: history never fires
        Assert.Empty(r.Server.Requests);

        r.Add(Chat("Alex", "hello chief", Alex, ts: 1790500100));
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));

        var req = Assert.Single(r.Server.Requests);
        Assert.Equal("Bearer " + Key, req.Auth);
        Assert.StartsWith("application/json", req.ContentType);
        Assert.Equal("""{"source":"chief-bridge-hook","channel":"crtest-x","chats":[{"nick":"Alex","trip":"Ab12Cd","text":"hello chief","ts":1790500100}]}""", req.Body);
        Assert.Equal(HookStep.Idle, await p.StepAsync(default));
        Assert.Single(r.Server.Requests);
        r.AssertNoSecrets();
    }

    [Fact]
    public async Task Own_nick_outbound_and_non_chat_frames_never_fire()
    {
        using var r = new Rig();
        var p = r.New();
        await p.StepAsync(default);
        r.Add(Chat("chief", "my own echo", "Q9a3Px") + Chat("", "x", dir: "out") + Chat("Alex", "outbound copy", dir: "out")
              + """{"ts":1,"dir":"in","msg":{"cmd":"onlineAdd","nick":"Fuse"}}""" + "\nnot json\n\n");
        Assert.Equal(HookStep.Idle, await p.StepAsync(default));
        Assert.Empty(r.Server.Requests);
    }

    [Fact]
    public async Task Trip_filter_fires_only_for_trusted_trips_and_sends_only_their_chats()
    {
        using var r = new Rig(h => h.Trips = ["!" + Alex]);
        var p = r.New();
        await p.StepAsync(default);

        r.Add(Chat("Mallory", "hi chief", "Zz99Zz") + Chat("Alex", "impostor, no trip"));
        Assert.Equal(HookStep.Idle, await p.StepAsync(default));
        Assert.Empty(r.Server.Requests);

        r.Add(Chat("Mallory", "again", "Zz99Zz") + Chat("Alex", "the real one", "!" + Alex));
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        var chats = r.Server.Last()["chats"]!.AsArray();
        Assert.Equal(new[] { "the real one" }, chats.Select(c => (string)c!["text"]!));
    }

    [Fact]
    public async Task Chats_inside_the_cooldown_are_held_and_go_out_together()
    {
        using var r = new Rig(h => h.CooldownSeconds = 15);
        var p = r.New();
        await p.StepAsync(default);
        r.Add(Chat("Alex", "hello", Alex));
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));

        r.Now = r.Now.AddSeconds(3);
        r.Add(Chat("Alex", "one", Alex));
        Assert.Equal(HookStep.Held, await p.StepAsync(default));
        r.Now = r.Now.AddSeconds(5);
        r.Add(Chat("Fuse", "two", "Xy34Zw") + Chat("Alex", "three", Alex));
        Assert.Equal(HookStep.Held, await p.StepAsync(default));
        Assert.Equal(3, p.Status.Pending);
        Assert.Single(r.Server.Requests);

        r.Now = r.Now.AddSeconds(7); // 15 s after the first fire
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        Assert.Equal(2, r.Server.Requests.Count);
        Assert.Equal(new[] { "one", "two", "three" }, r.Server.Last()["chats"]!.AsArray().Select(c => (string)c!["text"]!));
        Assert.Equal(0, p.Status.Pending);
    }

    [Fact]
    public async Task Failed_fires_are_retried_with_backoff_and_nothing_is_lost()
    {
        using var r = new Rig(h => { h.CooldownSeconds = 10; h.MaxRetrySeconds = 30; });
        var p = r.New();
        await p.StepAsync(default);
        r.Server.Responses.Enqueue(500);
        r.Server.Responses.Enqueue(401);

        r.Add(Chat("Alex", "please answer", Alex));
        Assert.Equal(HookStep.Failed, await p.StepAsync(default));
        Assert.Equal(("HTTP 500", 1, r.Now.ToUnixTimeSeconds() + 10), (p.Status.LastResult!, p.Status.Failures, p.Status.NextRetryAt!.Value));

        r.Now = r.Now.AddSeconds(9);
        Assert.Equal(HookStep.Held, await p.StepAsync(default));
        r.Now = r.Now.AddSeconds(1);
        Assert.Equal(HookStep.Failed, await p.StepAsync(default)); // 401
        Assert.Equal(r.Now.ToUnixTimeSeconds() + 20, p.Status.NextRetryAt); // doubled

        r.Now = r.Now.AddSeconds(20);
        r.Add(Chat("Alex", "hello?", Alex));
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        // The retry is its own POST, then the chat that arrived with it. Both are delivered, oldest first.
        Assert.Equal(4, r.Server.Requests.Count);
        Assert.Equal(new[] { "please answer" }, Texts(r.Server.Requests.ElementAt(2)));
        Assert.Equal(new[] { "hello?" }, Texts(r.Server.Requests.ElementAt(3)));
        Assert.Equal((0, 1L, 2L, "HTTP 200"), (p.Status.Failures, p.Status.FiredOk, p.Status.FiredFailed, p.Status.LastResult!));
        r.AssertNoSecrets();
    }

    [Fact]
    public async Task Backoff_is_capped_at_max_retry()
    {
        using var r = new Rig(h => { h.CooldownSeconds = 10; h.MaxRetrySeconds = 25; });
        var p = r.New();
        await p.StepAsync(default);
        for (var i = 0; i < 4; i++) r.Server.Responses.Enqueue(503);
        r.Add(Chat("Alex", "x", Alex));
        var delays = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HookStep.Failed, await p.StepAsync(default));
            delays.Add(p.Status.NextRetryAt!.Value - r.Now.ToUnixTimeSeconds());
            r.Now = DateTimeOffset.FromUnixTimeSeconds(p.Status.NextRetryAt!.Value);
        }

        Assert.Equal(new long[] { 10, 20, 25, 25 }, delays);
    }

    [Fact]
    public async Task Connection_refused_and_timeout_are_reported_by_kind_without_the_url()
    {
        var dead = $"http://127.0.0.1:{LocalHook.FreePort()}/hook/s3cr3t-path-9f2";
        using (var r = new Rig(url: dead))
        {
            var p = r.New();
            await p.StepAsync(default);
            r.Add(Chat("Alex", "x", Alex));
            Assert.Equal(HookStep.Failed, await p.StepAsync(default));
            Assert.StartsWith("error ", p.Status.LastResult);
            r.AssertNoSecrets();
        }

        using (var r = new Rig(h => h.TimeoutSeconds = 1))
        {
            r.Server.Delay = TimeSpan.FromSeconds(3);
            var p = r.New();
            await p.StepAsync(default);
            r.Add(Chat("Alex", "x", Alex));
            Assert.Equal(HookStep.Failed, await p.StepAsync(default));
            Assert.Equal("timeout", p.Status.LastResult);
            r.AssertNoSecrets();
        }
    }

    [Fact]
    public async Task Offset_survives_a_restart_and_held_chats_are_sent_by_the_next_process()
    {
        using var r = new Rig();
        var p1 = r.New();
        await p1.StepAsync(default);
        r.Add(Chat("Alex", "delivered", Alex));
        await p1.StepAsync(default);
        r.Now = r.Now.AddSeconds(2);
        r.Add(Chat("Alex", "held by cooldown", Alex));
        Assert.Equal(HookStep.Held, await p1.StepAsync(default));
        // process stops here (SIGTERM, crash): the held chat is past the saved offset on disk

        var p2 = r.New();
        Assert.Equal(HookStep.Fired, await p2.StepAsync(default)); // fresh cooldown in a new process
        Assert.Equal(new[] { "held by cooldown" }, r.Server.Last()["chats"]!.AsArray().Select(c => (string)c!["text"]!));
        Assert.Equal(2, r.Server.Requests.Count);
    }

    [Fact]
    public async Task Truncated_and_rotated_inboxes_start_over_and_partial_lines_wait()
    {
        using var r = new Rig(h => h.CooldownSeconds = 0);
        var p = r.New();
        await p.StepAsync(default);
        r.Add(Chat("Alex", "before rotation, padding padding padding padding padding", Alex));
        await p.StepAsync(default);

        File.WriteAllText(r.Inbox, Chat("Alex", "after truncation", Alex)); // shorter than the offset
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        Assert.Equal("after truncation", (string)r.Server.Last()["chats"]![0]!["text"]!);

        var big = string.Concat(Enumerable.Range(0, 5).Select(i => Chat("Fuse", $"rotated file line {i} with enough text to outgrow the old offset", "Xy34Zw")));
        File.WriteAllText(r.Inbox, big); // replaced by a different, longer file
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        Assert.Equal(5, r.Server.Last()["chats"]!.AsArray().Count);

        var line = Chat("Alex", "half written", Alex);
        r.Add(line[..20]);
        Assert.Equal(HookStep.Idle, await p.StepAsync(default));
        r.Add(line[20..]);
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        Assert.Equal("half written", (string)r.Server.Last()["chats"]![0]!["text"]!);
    }

    [Fact]
    public async Task Long_texts_and_big_batches_are_sent_in_pieces()
    {
        using var r = new Rig(h => { h.MaxText = 5; h.MaxBatch = 2; });
        var p = r.New();
        await p.StepAsync(default);
        r.Add(Chat("Alex", "first-long", Alex) + Chat("Alex", "second-long", Alex) + Chat("Alex", "third-long", Alex));
        await p.StepAsync(default);
        var bodies = r.Server.Requests.Select(q => (JsonObject)JsonNode.Parse(q.Body)!).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(new[] { "first", "secon" }, bodies[0]["chats"]!.AsArray().Select(c => (string)c!["text"]!));
        Assert.Equal(new[] { "third" }, bodies[1]["chats"]!.AsArray().Select(c => (string)c!["text"]!));
        Assert.All(bodies, b => Assert.Null(b["omitted"]));
    }

    [Fact]
    public async Task Large_backlog_stays_under_the_failing_size_and_a_failed_piece_does_not_block()
    {
        using var r = new Rig(h => { h.CooldownSeconds = 0; h.MaxRetrySeconds = 30; });
        var p = r.New();
        await p.StepAsync(default);

        // Long enough that one POST of the whole reconnect dump is over the size that returns HTTP 400.
        string TextFor(int i) => i == 40 ? "BLOCK" + new string('y', 695) : $"c{i:D2}" + new string('y', 696);
        var watched = Enumerable.Range(0, 83)
            .Select(i => new WatchedChat("Alex", Alex, TextFor(i), JsonValue.Create(1_790_500_100L + i)))
            .ToList();
        Assert.True(HookPoller.Utf8Bytes(p.BuildPayload(watched)) > HookPoller.MaxPayloadBytes);

        var failBlock = true;
        r.Server.StatusFor = body =>
        {
            if (Encoding.UTF8.GetByteCount(body) > HookPoller.MaxPayloadBytes)
                return 400;
            if (failBlock && body.Contains("\"text\":\"BLOCK", StringComparison.Ordinal))
                return 400;
            return null;
        };

        r.Add(string.Concat(Enumerable.Range(0, 83).Select(i => Chat("Alex", TextFor(i), Alex, ts: 1_790_500_100L + i))));
        Assert.Equal(HookStep.Failed, await p.StepAsync(default));

        Assert.True(r.Server.Requests.Count > 1);
        foreach (var req in r.Server.Requests)
        {
            var n = Encoding.UTF8.GetByteCount(req.Body);
            Assert.True(n <= HookPoller.MaxPayloadBytes, $"POST was {n} bytes");
            Assert.Null(((JsonObject)JsonNode.Parse(req.Body)!)["omitted"]);
        }

        var delivered = TextsOf(r.Server.Requests.Where(q => q.Status == 200)).ToList();
        var blocked = TextFor(40);
        Assert.Equal(watched.Select(c => c.Text).Where(t => t != blocked).OrderBy(t => t), delivered.OrderBy(t => t));
        Assert.DoesNotContain(blocked, delivered);
        Assert.Equal(1, p.Status.Pending);
        Assert.Contains("BLOCK", File.ReadAllText(r.Dir.File(".hook.offset.retry")));

        var beforeRetry = r.Server.Requests.Count;
        r.Add(Chat("Alex", "after the dump", Alex, ts: 1_790_500_999));
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        var fresh = r.Server.Requests.Skip(beforeRetry).ToList();
        var freshBody = Assert.Single(fresh);
        Assert.Equal(200, freshBody.Status);
        Assert.Equal(new[] { "after the dump" }, TextsOf(fresh));
        Assert.DoesNotContain("BLOCK", freshBody.Body);
        Assert.Equal(1, p.Status.Pending);

        failBlock = false;
        var p2 = r.New();
        Assert.Equal(HookStep.Fired, await p2.StepAsync(default));
        var allDelivered = TextsOf(r.Server.Requests.Where(q => q.Status == 200)).ToList();
        var expected = watched.Select(c => c.Text!).Append("after the dump").OrderBy(t => t).ToList();
        Assert.Equal(expected, allDelivered.OrderBy(t => t).ToList());
        Assert.Equal(0, p2.Status.Pending);
        Assert.False(File.Exists(r.Dir.File(".hook.offset.retry")));
        r.AssertNoSecrets();
    }

    [Fact]
    public async Task Pieces_keep_every_chat_oldest_first_under_both_caps()
    {
        using var r = new Rig(h => { h.CooldownSeconds = 0; h.MaxBatch = 3; });
        var p = r.New();
        p.PayloadByteLimit = 450;
        await p.StepAsync(default);

        var texts = new[]
        {
            "a-one", "b-two", "c-three", "d-four",
            new string('z', 800),
            "e-five"
        };
        r.Add(string.Concat(texts.Select((t, i) => Chat("Alex", t, Alex, ts: 1_790_500_100L + i))));
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));

        var bodies = r.Server.Requests.Select(q => q.Body).ToList();
        Assert.NotEmpty(bodies);
        var delivered = new List<string>();
        foreach (var body in bodies)
        {
            var root = (JsonObject)JsonNode.Parse(body)!;
            Assert.Null(root["omitted"]);
            var chats = root["chats"]!.AsArray();
            Assert.NotEmpty(chats);
            Assert.True(chats.Count <= 3);
            var bytes = Encoding.UTF8.GetByteCount(body);
            if (chats.Count > 1)
                Assert.True(bytes <= p.PayloadByteLimit, $"multi-chat POST was {bytes} bytes");
            delivered.AddRange(chats.Select(c => (string)c!["text"]!));
        }

        Assert.Equal(texts, delivered);
        Assert.Contains(texts[4], delivered);
        Assert.Equal(0, p.Status.Pending);
    }

    [Fact]
    public async Task Single_chat_400_is_not_split_and_does_not_block_the_rest()
    {
        using var r = new Rig(h => { h.CooldownSeconds = 0; h.MaxRetrySeconds = 30; });
        var p = r.New();
        await p.StepAsync(default);

        r.Server.StatusFor = body =>
        {
            var chats = ((JsonObject)JsonNode.Parse(body)!)["chats"]!.AsArray();
            if (chats.Count == 0)
                return 400;
            if (chats.Any(c => (string?)c!["text"] == "POISON"))
                return 400;
            return null;
        };

        r.Add(Chat("Alex", "POISON", Alex, ts: 1_790_500_100)
              + Chat("Alex", "good-a", Alex, ts: 1_790_500_101)
              + Chat("Alex", "good-b", Alex, ts: 1_790_500_102));
        Assert.Equal(HookStep.Failed, await p.StepAsync(default));

        Assert.All(r.Server.Requests, q =>
        {
            var root = (JsonObject)JsonNode.Parse(q.Body)!;
            Assert.NotEmpty(root["chats"]!.AsArray());
            Assert.Null(root["omitted"]);
        });
        Assert.InRange(r.Server.Requests.Count, 2, 4);
        Assert.Equal(new[] { "good-a", "good-b" }, TextsOf(r.Server.Requests.Where(q => q.Status == 200)).ToList());
        Assert.Equal(new[] { "POISON" }, RetryTexts(r));
        Assert.Contains("cannot split", r.Log.ToString());
        Assert.Equal(1, p.Status.Pending);

        // Alone, the same chat is still one POST. The next chat is not held behind it.
        var before = r.Server.Requests.Count;
        r.Add(Chat("Alex", "after", Alex, ts: 1_790_500_200));
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        var fresh = r.Server.Requests.Skip(before).ToList();
        var one = Assert.Single(fresh);
        Assert.Equal(200, one.Status);
        Assert.Equal(new[] { "after" }, Texts(one));
        Assert.DoesNotContain("POISON", one.Body);
        Assert.Equal(new[] { "POISON" }, RetryTexts(r));
        Assert.Equal(1, p.Status.Pending);
        r.AssertNoSecrets();
    }

    [Fact]
    public async Task Live_chats_while_a_retry_is_pending_are_not_captured_by_it()
    {
        using var r = new Rig(h => { h.CooldownSeconds = 10; h.MaxBatch = 1; h.MaxRetrySeconds = 60; });
        var p = r.New();
        await p.StepAsync(default);

        r.Server.StatusFor = body => body.Contains("\"text\":\"fail-piece\"", StringComparison.Ordinal) ? 500 : null;
        r.Add(Chat("Alex", "ok-piece", Alex, ts: 1_790_500_100) + Chat("Alex", "fail-piece", Alex, ts: 1_790_500_101));
        Assert.Equal(HookStep.Failed, await p.StepAsync(default));
        Assert.Equal(new[] { "ok-piece" }, TextsOf(r.Server.Requests.Where(q => q.Status == 200)).ToList());
        Assert.Equal(new[] { "fail-piece" }, RetryTexts(r));

        r.Now = r.Now.AddSeconds(4);
        r.Add(Chat("Alex", "while-waiting", Alex, ts: 1_790_500_110) + Chat("Fuse", "also-waiting", "Xy34Zw", ts: 1_790_500_111));
        Assert.Equal(HookStep.Held, await p.StepAsync(default));
        Assert.Equal(3, p.Status.Pending);
        Assert.Equal(2, r.Server.Requests.Count);

        var appended = 0;
        r.Server.StatusFor = body =>
        {
            if (body.Contains("\"text\":\"while-waiting\"", StringComparison.Ordinal) && Interlocked.Exchange(ref appended, 1) == 0)
                r.Add(Chat("Alex", "during-post", Alex, ts: 1_790_500_120));
            return body.Contains("\"text\":\"fail-piece\"", StringComparison.Ordinal) ? 500 : null;
        };

        r.Now = r.Now.AddSeconds(6);
        var before = r.Server.Requests.Count;
        Assert.Equal(HookStep.Failed, await p.StepAsync(default));
        var sent = r.Server.Requests.Skip(before).ToList();
        Assert.Equal(3, sent.Count);
        Assert.Equal(500, sent[0].Status);
        Assert.Equal(new[] { "fail-piece" }, Texts(sent[0]));
        Assert.Equal(200, sent[1].Status);
        Assert.Equal(new[] { "while-waiting" }, Texts(sent[1]));
        Assert.Equal(200, sent[2].Status);
        Assert.Equal(new[] { "also-waiting" }, Texts(sent[2]));
        Assert.All(sent.Skip(1), q => Assert.DoesNotContain("fail-piece", q.Body));
        Assert.Equal(new[] { "fail-piece" }, RetryTexts(r));
        Assert.Equal(1, p.Status.Pending);

        // The line appended during the live POST stays past the offset. The cooldown, not the retry, holds it.
        r.Now = r.Now.AddSeconds(10);
        Assert.Equal(HookStep.Fired, await p.StepAsync(default));
        Assert.Equal(new[] { "during-post" }, Texts(r.Server.Requests.Last()));
        Assert.DoesNotContain("fail-piece", r.Server.Requests.Last().Body);
        Assert.Equal(new[] { "fail-piece" }, RetryTexts(r));
        r.AssertNoSecrets();
    }

    private static IEnumerable<string> TextsOf(IEnumerable<(DateTime At, string? Auth, string? ContentType, string Body, int Status)> requests) =>
        requests.SelectMany(q => Texts(q));

    private static IEnumerable<string> Texts((DateTime At, string? Auth, string? ContentType, string Body, int Status) request) =>
        ((JsonObject)JsonNode.Parse(request.Body)!)["chats"]!.AsArray().Select(c => (string)c!["text"]!);

    private static List<string> RetryTexts(Rig r)
    {
        var path = r.Dir.File(".hook.offset.retry");
        if (!File.Exists(path))
            return new List<string>();
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => (string)((JsonObject)JsonNode.Parse(line)!)["text"]!)
            .ToList();
    }

    [Fact]
    public async Task Status_file_records_the_last_fire_and_classifies_as_running()
    {
        using var r = new Rig();
        var p = r.New();
        await p.StepAsync(default);
        r.Add(Chat("Alex", "x", Alex));
        await p.StepAsync(default);

        var s = HookStatus.TryRead(p.StatusPath)!;
        Assert.Equal(("running", Environment.ProcessId, "HTTP 200", true, 1), (s.State, s.Pid, s.LastResult!, s.LastOk!.Value, s.LastCount!.Value));
        var v = HookView.Classify(true, s, r.Now, ProcessInfo.IsRunning);
        Assert.Equal(HookState.Running, v.State);
        Assert.Contains("hook last fire:", HookView.LastFireLine(s, r.Now));
        Assert.Contains("HTTP 200, 1 chat(s)", HookView.LastFireLine(s, r.Now));
        r.AssertNoSecrets();
    }

    [Fact]
    public async Task Run_loop_fires_within_a_poll_and_stops_cleanly_on_cancellation()
    {
        using var r = new Rig(h => { h.PollSeconds = 5; h.CooldownSeconds = 0.5; });
        var p = new HookPoller(r.Cfg, r.Secrets, log: r.Log); // real clock
        using var cts = new CancellationTokenSource();
        var run = p.RunAsync(cts.Token);
        await Task.Delay(300);

        var sw = Stopwatch.StartNew();
        r.Add(Chat("Alex", "wake up", Alex));
        Assert.True(await r.Server.WaitForAsync(1, TimeSpan.FromSeconds(6)));
        var latency = sw.Elapsed;
        Assert.True(latency < TimeSpan.FromSeconds(5.5), $"took {latency}");

        // burst right after a fire: one more request with both lines once the 0.5 s cooldown ends
        r.Add(Chat("Alex", "b1", Alex));
        await Task.Delay(100);
        r.Add(Chat("Alex", "b2", Alex));
        Assert.True(await r.Server.WaitForAsync(2, TimeSpan.FromSeconds(6)));
        await Task.Delay(700);
        Assert.Equal(2, r.Server.Requests.Count);
        Assert.Equal(new[] { "b1", "b2" }, r.Server.Last()["chats"]!.AsArray().Select(c => (string)c!["text"]!));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var s = HookStatus.TryRead(p.StatusPath)!;
        Assert.Equal("stopped", s.State);
        Assert.Equal(HookState.NotRunning, HookView.Classify(true, s, DateTimeOffset.UtcNow, ProcessInfo.IsRunning).State);
        Assert.Contains("[chatbridge] hook: stopped", r.Log.ToString());
        p.Dispose();
        r.AssertNoSecrets();
    }

    [Fact]
    public async Task Heartbeat_is_refreshed_while_the_webhook_request_is_still_in_flight()
    {
        using var r = new Rig(h => { h.PollSeconds = 0.1; h.TimeoutSeconds = 300; });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new HookPoller(r.Cfg, r.Secrets, new SignalThenHoldHandler(entered, release.Task),
            () => DateTimeOffset.UtcNow, r.Log)
        {
            // Production waits 5 s, under the 15 s stale line. Shorter here so the test doesn't.
            HeartbeatWait = ct => Task.Delay(TimeSpan.FromMilliseconds(200), ct)
        };

        Assert.Equal(HookStep.Idle, await p.StepAsync(default));
        r.Add(Chat("Alex", "still waiting", Alex));
        var step = p.StepAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        HookStatus? status = null;
        for (var i = 0; i < 40 && status is null; i++)
        {
            await Task.Delay(50);
            status = HookStatus.TryRead(p.StatusPath);
        }

        try
        {
            Assert.False(step.IsCompleted);
            Assert.NotNull(status);
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - status!.HeartbeatAt;
            Assert.InRange(age, 0, 3);
            var view = HookView.Classify(true, status, DateTimeOffset.UtcNow, _ => true);
            Assert.Equal(HookState.Running, view.State);
        }
        finally
        {
            release.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK));
            await step.WaitAsync(TimeSpan.FromSeconds(5));
            p.Dispose();
        }
    }

    private sealed class SignalThenHoldHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered;
        private readonly Task<HttpResponseMessage> _response;

        public SignalThenHoldHandler(TaskCompletionSource entered, Task<HttpResponseMessage> response)
        {
            _entered = entered;
            _response = response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return await _response.WaitAsync(cancellationToken);
        }
    }
}

public class HookViewTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_790_500_000);

    private static HookStatus S(string state = "running", long hbAgo = 2, int failures = 0) => new()
    {
        Pid = 4242, State = state, StartedAt = T0.ToUnixTimeSeconds() - 100, HeartbeatAt = T0.ToUnixTimeSeconds() - hbAgo,
        StoppedAt = state == "stopped" ? T0.ToUnixTimeSeconds() - hbAgo : null, PollSeconds = 5, CooldownSeconds = 15,
        Failures = failures, LastResult = failures > 0 ? "HTTP 500" : null, NextRetryAt = failures > 0 ? T0.ToUnixTimeSeconds() + 30 : null
    };

    [Theory]
    [InlineData("running", 2, 0, true, "Running")]
    [InlineData("running", 2, 2, true, "Failing")]
    [InlineData("running", 2, 0, false, "NotRunning")]   // pid gone
    [InlineData("running", 25, 0, true, "NotRunning")]   // heartbeat stale (> 3 x 5 + 5)
    [InlineData("stopped", 1, 0, true, "NotRunning")]
    public void Classify(string state, long hbAgo, int failures, bool alive, string expected)
    {
        var v = HookView.Classify(true, S(state, hbAgo, failures), T0, _ => alive);
        Assert.Equal(Enum.Parse<HookState>(expected), v.State);
        if (expected == "Failing")
            Assert.Contains("last 2 fire(s) failed (last: HTTP 500), next retry in 30s", v.Detail);
    }

    [Fact]
    public void No_status_is_unknown_and_no_block_is_not_configured()
    {
        Assert.Equal(HookState.Unknown, HookView.Classify(true, null, T0, _ => true).State);
        Assert.Equal(HookState.NotConfigured, HookView.Classify(false, null, T0, _ => true).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"state":"running"}""")]
    public void Unusable_status_files_read_as_missing(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s"), content);
        Assert.Null(HookStatus.TryRead(dir.File("s")));
    }
}

/// <summary>`hook` through Program.Main, in process. Each test uses its own env var names so parallel tests
/// can't see each other's values.</summary>
public class HookCliTests
{
    private static (TempDir dir, string cfg) Deployment(string urlEnv, string authEnv)
    {
        var dir = new TempDir();
        var cfg = dir.File("config.json");
        File.WriteAllText(cfg, new JsonObject { ["channel"] = "c", ["nick"] = "chief", ["hook"] = new JsonObject { ["url_env"] = urlEnv, ["auth_env"] = authEnv } }.ToJsonString());
        File.WriteAllText(dir.File("inbox.jsonl"), "");
        return (dir, cfg);
    }

    [Fact]
    public async Task Missing_env_or_block_is_exit_2()
    {
        var (dir, cfg) = Deployment("CRT_URL_MISSING_" + Guid.NewGuid().ToString("N"), "CRT_AUTH_MISSING");
        using var _ = dir;
        Assert.Equal(2, await Program.Main(["hook", "--config", cfg]));
        File.WriteAllText(cfg, """{"channel":"c","nick":"chief"}""");
        Assert.Equal(2, await Program.Main(["hook", "--config", cfg]));
        Assert.Equal(2, await Program.Main(["hook", "--config", cfg, "--bogus"]));
    }

    [Fact]
    public async Task Test_mode_posts_one_fake_chat_and_reports_the_result()
    {
        using var server = new LocalHook();
        var id = Guid.NewGuid().ToString("N");
        var (dir, cfg) = Deployment("CRT_URL_" + id, "CRT_AUTH_" + id);
        using var _ = dir;
        Environment.SetEnvironmentVariable("CRT_URL_" + id, server.Url);
        Environment.SetEnvironmentVariable("CRT_AUTH_" + id, "testkey-" + id);
        try
        {
            Assert.Equal(0, await Program.Main(["hook", "--config", cfg, "--test"]));
            var req = Assert.Single(server.Requests);
            Assert.Equal("Bearer testkey-" + id, req.Auth);
            Assert.Contains("hook connectivity test", req.Body);

            server.Responses.Enqueue(403);
            Assert.Equal(HookOptions.ExitTestFailed, await Program.Main(["hook", "--config", cfg, "--test"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CRT_URL_" + id, null);
            Environment.SetEnvironmentVariable("CRT_AUTH_" + id, null);
        }
    }

    [Fact]
    public void The_offset_lock_is_exclusive_until_released()
    {
        using var dir = new TempDir();
        var path = HookInstanceLock.PathFor(dir.File(".hook.offset"));
        var first = HookInstanceLock.TryAcquire(path);
        Assert.NotNull(first);
        Assert.Null(HookInstanceLock.TryAcquire(path));
        first!.Dispose();
        using var second = HookInstanceLock.TryAcquire(path);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task A_held_lock_refuses_a_second_hook_even_when_status_is_missing()
    {
        using var server = new LocalHook();
        var id = Guid.NewGuid().ToString("N");
        var (dir, cfg) = Deployment("CRT_URL_" + id, "");
        using var _ = dir;
        Environment.SetEnvironmentVariable("CRT_URL_" + id, server.Url);
        using var gate = HookInstanceLock.TryAcquire(HookInstanceLock.PathFor(dir.File(".hook.offset")));
        Assert.NotNull(gate);
        try
        {
            Assert.False(File.Exists(dir.File(".hook.offset.status")));
            Assert.Equal(HookOptions.ExitAlreadyRunning, await Program.Main(["hook", "--config", cfg]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CRT_URL_" + id, null);
        }
    }

    [Fact]
    public async Task A_live_status_file_without_the_lock_does_not_block_another_poller()
    {
        using var server = new LocalHook();
        var id = Guid.NewGuid().ToString("N");
        var (dir, cfg) = Deployment("CRT_URL_" + id, "");
        using var _ = dir;
        using var other = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        new HookStatusWriter(dir.File(".hook.offset.status")).Write(new HookStatus
        {
            Pid = other.Id, State = "running", StartedAt = now, HeartbeatAt = now, PollSeconds = 5, CooldownSeconds = 15
        });

        var dll = typeof(Program).Assembly.Location;
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" hook --config \"{cfg}\"")
        {
            UseShellExecute = false
        };
        psi.Environment["CRT_URL_" + id] = server.Url;
        using var child = Process.Start(psi)!;
        try
        {
            var sw = Stopwatch.StartNew();
            HookStatus? s = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                s = HookStatus.TryRead(dir.File(".hook.offset.status"));
                if (s?.Pid == child.Id && s.State == "running")
                    break;
                if (child.HasExited)
                    break;
                await Task.Delay(50);
            }

            Assert.False(child.HasExited, child.HasExited ? $"hook exited {child.ExitCode} because of the status file" : "");
            Assert.Equal(child.Id, s?.Pid);
        }
        finally
        {
            try
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
                child.WaitForExit(2000);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            try { other.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task Status_accepts_state_and_rejects_unknown_arguments()
    {
        var (dir, cfg) = Deployment("CRT_UNUSED", "");
        using var _ = dir;
        Assert.Equal(0, await Program.Main(["status", "--config", cfg, "--state", dir.File("x.offset")]));
        Assert.Equal(2, await Program.Main(["status", "--config", cfg, "--bogus"]));
    }
}
