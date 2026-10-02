using System.Text.Json;

namespace Chief.Bridge.Tests;

public class ChatBridgeMilestoneTests
{
    private const string Secret = "SIDE-SECRET-9f3a";

    private static string AgentsJson(bool enabled = true) =>
        """
        {
          "channel": "room",
          "nick": "relay",
          "base": ".",
          "mentions": { "enabled": 
        """ + (enabled ? "true" : "false") + """
        , "max_fanout_hop": 1 },
          "agents": [
            { "id": "dot", "nicks": ["dot"] },
            { "id": "muse", "nicks": ["muse", "fuse"] }
          ]
        }
        """;

    private static (RelayConfig Cfg, MentionRouter Router) Open(TempDir dir, string? json = null)
    {
        var path = dir.File("config.json");
        File.WriteAllText(path, json ?? AgentsJson());
        var cfg = RelayConfig.Load(path, false, dir.Path, _ => null);
        return (cfg, MentionRouter.Open(cfg));
    }

    private static RoomMessage Chat(string from, string text, long ts = 100, string? serverId = null) =>
        new("room", from, null, text, ts, serverId);

    [Fact]
    public void Restart_reloads_pending_events_in_order_and_keeps_acks()
    {
        using var dir = new TempDir();
        var (_, router) = Open(dir);
        var first = router.Route(Chat("Alex", "@dot one", 1));
        var second = router.Route(Chat("Alex", "@dot two", 2));
        Assert.Equal(RouteKind.Delivered, first.Kind);
        Assert.Equal(1, Assert.Single(first.Delivered).Seq);
        Assert.Equal(2, Assert.Single(second.Delivered).Seq);
        var firstId = first.Delivered[0].Id;
        var secondId = second.Delivered[0].Id;

        var reloaded = MentionRouter.Open(routerConfig(dir));
        var pending = reloaded.InboxFor("dot").Pending();
        Assert.Equal(new[] { firstId, secondId }, pending.Select(v => v.Event.Id));
        Assert.Equal(new long[] { 1, 2 }, pending.Select(v => v.Event.Seq));

        var when = DateTimeOffset.FromUnixTimeSeconds(500);
        Assert.True(reloaded.InboxFor("dot").Ack(firstId, when));
        var afterAck = MentionRouter.Open(routerConfig(dir)).InboxFor("dot").Pending();
        var only = Assert.Single(afterAck);
        Assert.Equal(secondId, only.Event.Id);
        Assert.Equal(2, only.Event.Seq);
    }

    [Fact]
    public void Replay_of_the_same_message_is_suppressed_across_restart()
    {
        using var dir = new TempDir();
        var (_, router) = Open(dir);
        var message = Chat("Alex", "@dot again", 7, serverId: "srv-7");
        Assert.Equal(RouteKind.Delivered, router.Route(message).Kind);
        Assert.Equal(RouteKind.Duplicate, router.Route(message).Kind);

        var again = MentionRouter.Open(routerConfig(dir));
        Assert.Equal(RouteKind.Duplicate, again.Route(message).Kind);
        Assert.Single(File.ReadAllLines(again.InboxFor("dot").InboxPath).Where(l => l.Length > 0));
    }

    [Fact]
    public void Multiple_mentions_deliver_once_to_each_agent()
    {
        using var dir = new TempDir();
        var (_, router) = Open(dir);
        var result = router.Route(Chat("Alex", "@muse @dot please look", 3));

        Assert.Equal(RouteKind.Delivered, result.Kind);
        Assert.Equal(new[] { "muse", "dot" }, result.Delivered.Select(e => e.Agent));
        Assert.Equal(result.Delivered[0].SourceId, result.Delivered[1].SourceId);
        Assert.NotEqual(result.Delivered[0].Id, result.Delivered[1].Id);
        Assert.Equal(new[] { "muse", "dot" }, result.Delivered[0].Mentions);

        var aliases = router.Route(Chat("Alex", "@muse and @fuse", 4));
        Assert.Equal("muse", Assert.Single(aliases.Delivered).Agent);

        Assert.Equal(RouteKind.Duplicate, router.Route(Chat("Alex", "@muse @dot please look", 3)).Kind);
        Assert.Single(router.InboxFor("dot").Pending());
        Assert.Equal(2, router.InboxFor("muse").Pending().Count);
    }

    [Fact]
    public void Self_messages_and_agent_replies_do_not_fan_out_unless_requested()
    {
        using var dir = new TempDir();
        var (_, router) = Open(dir);

        Assert.Equal(RouteKind.OwnEcho, router.Route(Chat("relay", "@dot from the bridge", 1)).Kind);
        Assert.Equal(RouteKind.ReplySuppressed, router.Route(Chat("dot", "@dot @muse status", 2)).Kind);
        Assert.Equal(RouteKind.SelfOnly, router.Route(Chat("dot", "!fanout @dot only me", 3)).Kind);

        var fanout = router.Route(Chat("dot", "!fanout @muse take a look", 4));
        var delivered = Assert.Single(fanout.Delivered);
        Assert.Equal("muse", delivered.Agent);
        Assert.True(delivered.Fanout);
        Assert.Equal(1, delivered.Hop);

        var bounded = router.Route(Chat("Dot", """{"fanout":true,"hop":1,"text":"@muse again"}""", 5));
        Assert.Equal(RouteKind.HopLimited, bounded.Kind);

        Assert.False(File.Exists(router.InboxFor("dot").InboxPath));
        Assert.Single(router.InboxFor("muse").Pending());
    }

    [Fact]
    public async Task Adapter_failure_is_a_soft_path_with_backoff_and_ordering()
    {
        using var dir = new TempDir();
        var (_, router) = Open(dir);
        router.Route(Chat("Alex", "@dot first", 1));
        router.Route(Chat("Alex", "@dot second", 2));
        var inbox = router.InboxFor("dot");
        var head = inbox.Pending()[0].Event.Id;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000);
        var adapter = new FlakyAdapter(head);

        var failed = await new InboxWakePump(inbox).PumpAsync(adapter, now, CancellationToken.None);

        Assert.True(failed.SoftFailed);
        Assert.Equal(head, failed.SoftFailedId);
        Assert.Equal(nameof(InvalidOperationException), failed.Error);
        Assert.Equal(0, failed.Acked);
        var held = inbox.Pending();
        Assert.Equal(2, held.Count);
        Assert.Equal(1, held[0].Attempts);
        Assert.Equal(now.ToUnixTimeSeconds() + 1, held[0].NextUnix);
        Assert.Empty(inbox.Due(now));
        Assert.DoesNotContain(Secret, File.ReadAllText(Path.Combine(dir.Path, "agents", "dot", "control.jsonl")));

        var recovered = await new InboxWakePump(inbox).PumpAsync(adapter, now.AddSeconds(1), CancellationToken.None);

        Assert.False(recovered.SoftFailed);
        Assert.Equal(2, recovered.Acked);
        Assert.Empty(inbox.Pending());
        Assert.Equal(2, adapter.Seen.Count);
        Assert.Equal(new long[] { 1, 2 }, adapter.Seen.Select(r => r.Seq));
        Assert.All(adapter.Seen, r => Assert.Equal(InboxContract.RoomScope, r.Scope));
    }

    [Fact]
    public void Private_side_context_stays_out_of_the_room()
    {
        using var dir = new TempDir();
        var (_, router) = Open(dir);
        var sidePath = Path.Combine(dir.Path, "agents", "dot", "side", "alex.jsonl");
        router.Conversations.AppendSide("dot", "alex", "alex", Secret, 9);
        var before = File.ReadAllBytes(sidePath);

        router.Route(Chat("Alex", "@dot public question", 8));
        var reply = router.Conversations.ComposeRoomReply("public answer");
        var after = File.ReadAllBytes(sidePath);

        Assert.Equal("public answer", reply);
        Assert.Equal(before, after);
        var room = router.Conversations.Read("dot", InboxContract.RoomScope, null);
        Assert.DoesNotContain(Secret, Assert.Single(room).Text);
        Assert.Contains(Secret, Assert.Single(router.Conversations.Read("dot", InboxContract.SideScope, "alex")).Text);
        Assert.DoesNotContain(Secret, File.ReadAllText(Path.Combine(dir.Path, "agents", "dot", "room.jsonl")));
        Assert.DoesNotContain(Secret, File.ReadAllText(router.InboxFor("dot").InboxPath));
        var wake = JsonSerializer.Serialize(InboxWakeRequest.Create(router.InboxFor("dot").Pending()[0]), JsonUtil.Opts);
        Assert.DoesNotContain(Secret, wake);
        Assert.Contains("\"scope\":\"room\"", wake);
        Assert.Throws<InvalidOperationException>(() => InboxContract.RefuseSideCopy(InboxContract.SideScope));
    }

    [Fact]
    public async Task Rename_and_config_aliases_keep_working()
    {
        Assert.Equal("ChatBridge", ProductInfo.Name);
        Assert.Equal("Chief.Bridge", ProductInfo.LegacyName);
        Assert.Equal("chat-bridge", ProductInfo.Command);
        Assert.Equal("chief-bridge", ProductInfo.LegacyCommand);
        Assert.Equal("chatbridge", ProductInfo.LogTag);
        Assert.NotEqual("chief", ProductInfo.LogTag);

        var (code, stdout, _) = await RunAsync("help");
        Assert.Equal(0, code);
        Assert.Contains("ChatBridge", stdout);
        Assert.Contains("chief-bridge", stdout);
        Assert.Contains("Chief.Bridge", stdout);
        Assert.Contains("src/Chief.Bridge", stdout);
        Assert.Contains("src/ChatBridge", stdout);
        Assert.Contains("inbox due", stdout);

        var repo = RepoRoot();
        var chatProj = File.ReadAllText(Path.Combine(repo, "src", "ChatBridge", "ChatBridge.csproj"));
        var legacyProj = File.ReadAllText(Path.Combine(repo, "src", "Chief.Bridge", "Chief.Bridge.csproj"));
        Assert.Contains("<ToolCommandName>chat-bridge</ToolCommandName>", chatProj);
        Assert.Contains("<AssemblyName>ChatBridge</AssemblyName>", chatProj);
        Assert.Contains("<ToolCommandName>chief-bridge</ToolCommandName>", legacyProj);
        Assert.Contains("<AssemblyName>Chief.Bridge</AssemblyName>", legacyProj);
        Assert.Contains(@"..\ChatBridge\*.cs", legacyProj);

        using var dir = new TempDir();
        var path = dir.File("from-env.json");
        File.WriteAllText(path, "{\"channel\":\"from-env\",\"nick\":\"dot\"}");
        var fromNew = RelayConfig.Load(null, false, dir.Path, name => name == ProductInfo.ConfigEnv ? path : null);
        Assert.Equal("dot", fromNew.Nick);
        Assert.Equal(ProductInfo.ConfigEnv, fromNew.Source);
        Assert.Empty(fromNew.Roster.Agents);
        Assert.False(fromNew.MentionRouting.Enabled);

        var fromLegacy = RelayConfig.Load(null, false, dir.Path, name =>
            name == ProductInfo.LegacyConfigEnv ? path : name == ProductInfo.ConfigEnv ? dir.File("missing.json") : null);
        Assert.Equal(ProductInfo.LegacyConfigEnv, fromLegacy.Source);

        var routed = Open(dir);
        Assert.Equal(RouteKind.NoMention, routed.Router.Route(Chat("Alex", "hey chief", 1)).Kind);
        Assert.Equal(RouteKind.NoMention, routed.Router.Route(Chat("Alex", "@chief", 2)).Kind);
        Assert.Equal("dot", Assert.Single(routed.Router.Route(Chat("Alex", "@dot", 3)).Delivered).Agent);
        Assert.DoesNotContain(routed.Cfg.Roster.Agents, a => a.Id == "chief");
    }

    [Fact]
    public async Task Inbox_cli_acks_and_retries_without_removing_the_jsonl_bridge()
    {
        using var dir = new TempDir();
        var (cfg, router) = Open(dir);
        router.Route(Chat("Alex", "@dot via cli", 11));
        var id = router.InboxFor("dot").Pending()[0].Event.Id;

        var (dueCode, dueOut, _) = await RunAsync("inbox", "due", "--config", cfg.ConfigPath, "--agent", "dot");
        Assert.Equal(0, dueCode);
        Assert.Contains(InboxContract.Name, dueOut);
        Assert.Contains(id, dueOut);

        var beforeFail = DateTimeOffset.UtcNow;
        var (failCode, failOut, _) = await RunAsync("inbox", "fail", "--agent", "dot", id, "--config", cfg.ConfigPath);
        Assert.Equal(0, failCode);
        Assert.Contains("retry", failOut);

        var immediate = MentionRouter.Open(cfg).InboxFor("dot");
        var waiting = Assert.Single(immediate.Pending());
        Assert.Equal(1, waiting.Attempts);
        Assert.True(waiting.NextUnix >= beforeFail.ToUnixTimeSeconds() + 1);
        Assert.Empty(immediate.Due(beforeFail));

        var (ackCode, ackOut, _) = await RunAsync("inbox", "ack", "--agent", "dot", "--config", cfg.ConfigPath, id);
        Assert.Equal(0, ackCode);
        Assert.Contains("acked", ackOut);
        Assert.Empty(MentionRouter.Open(cfg).InboxFor("dot").Pending());

        var (missing, _, err) = await RunAsync("inbox", "ack", "--agent", "dot", "--config", cfg.ConfigPath, "nope");
        Assert.Equal(1, missing);
        Assert.Contains("unknown event", err);

        Assert.False(File.Exists(Path.Combine(dir.Path, "inbox.jsonl")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "outbox.jsonl")));
    }

    [Fact]
    public void Disabled_mentions_leave_the_jsonl_bridge_alone()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, AgentsJson(enabled: false));
        var cfg = RelayConfig.Load(path, false, dir.Path, _ => null);
        var bridge = new HackChatBridge(cfg, new BridgeRuntime());

        Assert.False(cfg.MentionRouting.Enabled);
        Assert.False(Directory.Exists(Path.Combine(cfg.BaseDir, "agents")));
        Assert.NotNull(bridge);
    }

    [Fact]
    public void Bad_agent_config_fails_closed()
    {
        using var dir = new TempDir();
        var path = dir.File("bad.json");
        File.WriteAllText(path, """{"channel":"c","nick":"n","agents":[{"id":"../x","nicks":["a"]}]}""");
        var ex = Assert.Throws<ConfigException>(() => RelayConfig.Load(path, false, dir.Path, _ => null));
        Assert.Contains("agent id", ex.Message);
    }

    [Fact]
    public void Explicit_task_address_routes_and_a_bare_word_or_email_does_not()
    {
        using var dir = new TempDir();
        var (_, router) = Open(dir);

        Assert.Equal(RouteKind.NoMention, router.Route(Chat("Alex", "hey dot", 1)).Kind);
        Assert.Equal(RouteKind.NoMention, router.Route(Chat("Alex", "mail dot@muse", 2)).Kind);
        Assert.Equal("dot", Assert.Single(router.Route(Chat("Alex", """{"type":"task","to":"dot","text":"please"}""", 3)).Delivered).Agent);
        Assert.Equal("muse", Assert.Single(router.Route(Chat("Alex", "TASK to muse: look", 4)).Delivered).Agent);
    }

    [Fact]
    public void Mention_ingress_swallows_sink_failures()
    {
        var ok = MentionIngress.TryAccept(new ThrowingSink(), Chat("Alex", "@dot", 1), out var error);

        Assert.False(ok);
        Assert.Equal("not filed: " + nameof(IOException), error);
    }

    private static RelayConfig routerConfig(TempDir dir) =>
        RelayConfig.Load(dir.File("config.json"), false, dir.Path, _ => null);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseChiefRelay.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = await Program.Main(args);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    private sealed class FlakyAdapter(string failOnceId) : IAgentWakeAdapter
    {
        private bool _failed;
        public List<InboxWakeRequest> Seen { get; } = new();

        public ValueTask WakeAsync(InboxWakeRequest request, CancellationToken cancellationToken)
        {
            if (!_failed && request.Id == failOnceId)
            {
                _failed = true;
                throw new InvalidOperationException(Secret);
            }

            Seen.Add(request);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IMentionSink
    {
        public void Accept(RoomMessage message) => throw new IOException("disk");
    }
}
