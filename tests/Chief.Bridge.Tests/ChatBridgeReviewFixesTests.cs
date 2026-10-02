namespace Chief.Bridge.Tests;

/// <summary>
/// Regressions for the three filesystem blockers on the mention inbox, and for the wake
/// contract keeping a sender trip as untrusted evidence.
/// </summary>
public class ChatBridgeReviewFixesTests
{
    private const string Agents = """
        {
          "channel": "room",
          "nick": "relay",
          "base": ".",
          "mentions": { "enabled": true, "max_fanout_hop": 1 },
          "agents": [
            { "id": "dot", "nicks": ["dot"] },
            { "id": "muse", "nicks": ["muse"] }
          ]
        }
        """;

    [Fact]
    public async Task Lock_contention_retries_and_files_the_mention()
    {
        using var dir = new TempDir();
        var router = Open(dir);
        var lockPath = Path.Combine(dir.Path, "agents", "dot", "inbox.lock");
        var message = new RoomMessage("room", "Alex", null, "@dot while the lock is held", 11, "lock-1");

        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string? error = null;
        var entered = new ManualResetEventSlim(false);
        var pending = Task.Run(() =>
        {
            entered.Set();
            return MentionIngress.TryAccept(router, message, out error);
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(50);
        await held.DisposeAsync();

        Assert.True(await pending);
        Assert.Null(error);
        var filed = Assert.Single(router.InboxFor("dot").Pending());
        Assert.Equal("srv:lock-1", filed.Event.SourceId);
        Assert.Contains("while the lock is held", filed.Event.Text);
    }

    [Fact]
    public void Lock_contention_past_the_budget_is_surfaced_as_not_filed()
    {
        using var dir = new TempDir();
        var router = Open(dir, TimeSpan.FromMilliseconds(80));
        var lockPath = Path.Combine(dir.Path, "agents", "dot", "inbox.lock");
        var message = new RoomMessage("room", "Alex", null, "@dot still locked", 12, "lock-2");

        bool accepted;
        string? error;
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            accepted = MentionIngress.TryAccept(router, message, out error);

        Assert.False(accepted);
        Assert.Equal("not filed: IOException", error);
        Assert.Empty(router.InboxFor("dot").Pending());
    }

    [Fact]
    public void Torn_final_line_does_not_eat_the_next_event_or_ack()
    {
        using var dir = new TempDir();
        var inbox = AgentInbox.Open(Path.Combine(dir.Path, "dot"));
        Assert.True(inbox.TryAppend(Draft("dot:one", "one", "first"), out var first));

        File.AppendAllText(inbox.InboxPath, "{\"id\":\"torn-event\"");
        Assert.True(inbox.TryAppend(Draft("dot:two", "two", "second"), out var second));

        var control = Path.Combine(dir.Path, "dot", "control.jsonl");
        File.AppendAllText(control, "{\"op\":\"ack\",\"id\":\"torn-ack\"");
        var when = DateTimeOffset.FromUnixTimeSeconds(50);
        Assert.True(inbox.Ack(first.Id, when));

        var reloaded = AgentInbox.Open(Path.Combine(dir.Path, "dot"));
        var pending = reloaded.Pending();
        var only = Assert.Single(pending);
        Assert.Equal(second.Id, only.Event.Id);
        Assert.Equal("second", only.Event.Text);
        Assert.DoesNotContain("torn-event", File.ReadAllText(reloaded.InboxPath));
        Assert.Contains(second.Id, File.ReadAllText(reloaded.InboxPath));
        Assert.Contains(first.Id, File.ReadAllText(control));
    }

    [Fact]
    public void Alternating_fanout_replies_do_not_reset_hop()
    {
        using var dir = new TempDir();
        var router = Open(dir);
        var first = router.Route(new RoomMessage("room", "dot", null, "!fanout @muse ping", 1, null));
        Assert.Equal(1, Assert.Single(first.Delivered).Hop);

        var reset = router.Route(new RoomMessage("room", "muse", null, "!fanout @dot pong", 2, null));
        Assert.Equal(RouteKind.HopLimited, reset.Kind);
        Assert.Empty(router.InboxFor("dot").Pending());

        var explicitZero = router.Route(new RoomMessage("room", "muse", null,
            """{"fanout":true,"hop":0,"text":"@dot again"}""", 3, null));
        Assert.Equal(RouteKind.HopLimited, explicitZero.Kind);

        var restarted = Open(dir);
        var afterRestart = restarted.Route(new RoomMessage("room", "muse", null, "!fanout @dot later", 4, null));
        Assert.Equal(RouteKind.HopLimited, afterRestart.Kind);

        using var wider = new TempDir();
        var deep = Open(wider, json: Agents.Replace("\"max_fanout_hop\": 1", "\"max_fanout_hop\": 2", StringComparison.Ordinal));
        deep.Route(new RoomMessage("room", "dot", null, "!fanout @muse one", 1, null));
        var second = deep.Route(new RoomMessage("room", "muse", null, "!fanout @dot two", 2, null));
        Assert.Equal(2, Assert.Single(second.Delivered).Hop);
    }

    [Fact]
    public void Wake_keeps_sender_trip_as_untrusted_evidence()
    {
        using var dir = new TempDir();
        var router = Open(dir);
        var carried = router.Route(new RoomMessage("room", "Alex", "Ab12Cd", "@dot with a trip", 21, "trip-1"));
        var missing = router.Route(new RoomMessage("room", "Alex", null, "@dot no trip", 22, "trip-2"));
        Assert.Equal(RouteKind.Delivered, carried.Kind);
        Assert.Equal(RouteKind.Delivered, missing.Kind);

        var wakes = router.InboxFor("dot").Pending().Select(InboxContract.ToWake).ToList();
        var withTrip = Assert.Single(wakes, w => w.SourceId == "srv:trip-1");
        var without = Assert.Single(wakes, w => w.SourceId == "srv:trip-2");
        Assert.Equal("Ab12Cd", withTrip.Trip);
        Assert.Null(without.Trip);

        var json = System.Text.Json.JsonSerializer.Serialize(withTrip, JsonUtil.Opts);
        Assert.Contains("\"trip\":\"Ab12Cd\"", json);
        Assert.DoesNotContain("trusted", json, StringComparison.OrdinalIgnoreCase);

        var acker = new AutoAcker(new AutoAckConfig
        {
            Enabled = true,
            MentionTrips = ["OnlyOnTheList"]
        }, "relay");
        var decision = acker.Consider("Alex", withTrip.Trip, "@relay hi", DateTimeOffset.UnixEpoch,
            () => new HookView(HookState.NotConfigured, "not configured", null));
        Assert.False(decision.Send);
        Assert.Equal("untrusted trip", decision.Reason);
    }

    private static MentionRouter Open(TempDir dir, TimeSpan? lockBudget = null, string? json = null)
    {
        var path = dir.File("config.json");
        File.WriteAllText(path, json ?? Agents);
        var cfg = RelayConfig.Load(path, false, dir.Path, _ => null);
        return MentionRouter.Open(cfg, lockBudget);
    }

    private static InboxEvent Draft(string id, string source, string text) => new()
    {
        Id = id,
        Agent = "dot",
        SourceId = source,
        Room = "room",
        From = "Alex",
        Text = text,
        Mentions = ["dot"],
        Scope = InboxContract.RoomScope
    };
}
