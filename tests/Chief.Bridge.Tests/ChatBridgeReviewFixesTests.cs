using System.Text;

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
    public void Lock_timeout_keeps_the_mention_pending_after_release()
    {
        using var dir = new TempDir();
        var router = Open(dir, TimeSpan.FromMilliseconds(80));
        var inbox = router.InboxFor("dot");
        var lockPath = Path.Combine(dir.Path, "agents", "dot", "inbox.lock");
        var deferred = Path.Combine(dir.Path, "agents", "dot", AgentInbox.DeferredName);
        var message = new RoomMessage("room", "Alex", null, "@dot still locked", 12, "lock-2");

        bool accepted;
        string? error;
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            accepted = MentionIngress.TryAccept(router, message, out error);
            Assert.Contains("still locked", File.ReadAllText(deferred));
            if (File.Exists(inbox.InboxPath))
                Assert.DoesNotContain("still locked", File.ReadAllText(inbox.InboxPath));
        }

        Assert.True(accepted);
        Assert.Null(error);
        var filed = Assert.Single(inbox.Pending());
        Assert.Equal("srv:lock-2", filed.Event.SourceId);
        Assert.Contains("still locked", filed.Event.Text);
        Assert.Equal(0, new FileInfo(deferred).Length);
        Assert.Contains("still locked", File.ReadAllText(Path.Combine(dir.Path, "agents", "dot", "room.jsonl")));
        Assert.Equal(RouteKind.Duplicate, router.Route(message).Kind);
        Assert.Single(inbox.Pending());
    }

    [Fact]
    public void Lock_timeout_mention_is_still_pending_after_restart()
    {
        using var dir = new TempDir();
        var router = Open(dir, TimeSpan.FromMilliseconds(80));
        var lockPath = Path.Combine(dir.Path, "agents", "dot", "inbox.lock");
        var deferred = Path.Combine(dir.Path, "agents", "dot", AgentInbox.DeferredName);
        var message = new RoomMessage("room", "Alex", null, "@dot held across restart", 13, "lock-3");

        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var routed = router.Route(message);
            Assert.Equal(RouteKind.Deferred, routed.Kind);
            Assert.Empty(routed.Delivered);
            Assert.Contains("held across restart", File.ReadAllText(deferred));
        }

        var restarted = Open(dir, TimeSpan.FromMilliseconds(80));
        var filed = Assert.Single(restarted.InboxFor("dot").Pending());
        Assert.Equal("srv:lock-3", filed.Event.SourceId);
        Assert.Contains("held across restart", filed.Event.Text);
        Assert.Equal(0, new FileInfo(deferred).Length);
        Assert.Equal(RouteKind.Duplicate, restarted.Route(message).Kind);
    }

    [Fact]
    public void Torn_final_line_does_not_eat_the_next_event_or_ack()
    {
        using var dir = new TempDir();
        var inbox = AgentInbox.Open(Path.Combine(dir.Path, "dot"));
        Assert.True(inbox.TryAppend(Draft("dot:one", "one", "first"), out var first));

        var prefix = File.ReadAllBytes(inbox.InboxPath);
        File.AppendAllText(inbox.InboxPath, "{\"id\":\"torn-event\"");
        Assert.True(inbox.TryAppend(Draft("dot:two", "two", "second"), out var second));
        Assert.True(File.ReadAllBytes(inbox.InboxPath).AsSpan(0, prefix.Length).SequenceEqual(prefix));

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
    public void Torn_tail_repair_keeps_the_prefix_bytes()
    {
        using var dir = new TempDir();
        var path = dir.File("log.jsonl");
        var prefix = Encoding.UTF8.GetBytes("{\"id\":\"keep\",\"source_id\":\"keep\",\"text\":\"safe\"}\n");
        var torn = Encoding.UTF8.GetBytes("{\"id\":\"torn\"");
        var whole = new byte[prefix.Length + torn.Length];
        prefix.CopyTo(whole, 0);
        torn.CopyTo(whole, prefix.Length);
        File.WriteAllBytes(path, whole);

        AgentInbox.RepairTornTail(path);

        Assert.Equal(prefix, File.ReadAllBytes(path));

        var complete = Encoding.UTF8.GetBytes("{\"id\":\"whole\",\"source_id\":\"whole\"}");
        File.WriteAllBytes(path, complete);
        AgentInbox.RepairTornTail(path);
        var terminated = new byte[complete.Length + 1];
        complete.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        Assert.Equal(terminated, File.ReadAllBytes(path));

        AgentInbox.RepairTornTail(path);
        Assert.Equal(terminated, File.ReadAllBytes(path));

        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("{\"id\":"));
        AgentInbox.RepairTornTail(path);
        Assert.Empty(File.ReadAllBytes(path));
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
    public void Old_fanout_hop_does_not_block_an_unrelated_human_task()
    {
        using var dir = new TempDir();
        var router = Open(dir);
        var old = router.Route(new RoomMessage("room", "dot", null, "!fanout @muse old chain", 1, "old"));
        var oldEvent = Assert.Single(old.Delivered);
        Assert.Equal(1, oldEvent.Hop);
        Assert.Equal("srv:old", oldEvent.Root);

        var muse = router.InboxFor("muse");
        Assert.True(muse.Ack(Assert.Single(muse.Pending()).Event.Id, DateTimeOffset.UnixEpoch));

        var restarted = Open(dir);
        var stillOld = restarted.Route(new RoomMessage("room", "muse", null, "!fanout @dot same chain", 2, "same"));
        Assert.Equal(RouteKind.HopLimited, stillOld.Kind);

        var human = restarted.Route(new RoomMessage("room", "Alex", null, "@muse new task", 3, "new"));
        var humanEvent = Assert.Single(human.Delivered);
        Assert.Equal(0, humanEvent.Hop);
        Assert.Null(humanEvent.Parent);
        Assert.Equal("srv:new", humanEvent.Root);
        Assert.NotEqual(oldEvent.Root, humanEvent.Root);

        var fan = restarted.Route(new RoomMessage("room", "muse", null, "!fanout @dot about the new task", 4, "about"));
        var fanEvent = Assert.Single(fan.Delivered);
        Assert.Equal(RouteKind.Delivered, fan.Kind);
        Assert.Equal(1, fanEvent.Hop);
        Assert.Equal("srv:new", fanEvent.Parent);
        Assert.Equal("srv:new", fanEvent.Root);
        Assert.Equal("dot", fanEvent.Agent);

        var wake = InboxContract.ToWake(Assert.Single(restarted.InboxFor("dot").Pending()));
        Assert.Equal("srv:new", wake.Parent);
        Assert.Equal("srv:new", wake.Root);
        Assert.Equal(1, wake.Hop);

        var continued = restarted.Route(new RoomMessage("room", "muse", null,
            """{"fanout":true,"parent":"srv:old","text":"@dot still the old chain"}""", 5, "cont"));
        Assert.Equal(RouteKind.HopLimited, continued.Kind);

        var claimed = restarted.Route(new RoomMessage("room", "muse", null,
            """{"fanout":true,"hop":0,"parent":"srv:old","text":"@dot hop zero"}""", 6, "zero"));
        Assert.Equal(RouteKind.HopLimited, claimed.Kind);

        var again = Open(dir);
        var afterRestart = again.Route(new RoomMessage("room", "muse", null, "!fanout @dot latest is the new task", 7, "later"));
        Assert.Equal(1, Assert.Single(afterRestart.Delivered).Hop);
        Assert.Equal("srv:new", Assert.Single(afterRestart.Delivered).Root);

        var blockedRecipient = again.Route(new RoomMessage("room", "dot", null, "!fanout @muse continue the new task", 8, "deep"));
        Assert.Equal(RouteKind.HopLimited, blockedRecipient.Kind);
    }

    [Fact]
    public void Sender_inbox_lock_retains_fanout_until_the_lock_is_free()
    {
        using var dir = new TempDir();
        var router = Open(dir, TimeSpan.FromMilliseconds(80));
        var cause = router.Route(new RoomMessage("room", "Alex", null, "@muse hold this", 10, "cause"));
        Assert.Equal("srv:cause", Assert.Single(cause.Delivered).Root);

        var message = new RoomMessage("room", "muse", null, "!fanout @dot after the lock", 11, "carry");
        var ingress = Path.Combine(dir.Path, "agents", IngressJournal.FileName);
        var dotInbox = Path.Combine(dir.Path, "agents", "dot", "inbox.jsonl");
        bool accepted;
        string? error;
        using (new FileStream(Path.Combine(dir.Path, "agents", "muse", "inbox.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            accepted = MentionIngress.TryAccept(router, message, out error);
            Assert.Contains("after the lock", File.ReadAllText(ingress));
            if (File.Exists(dotInbox))
                Assert.DoesNotContain("after the lock", File.ReadAllText(dotInbox));
            var senderDeferred = Path.Combine(dir.Path, "agents", "muse", AgentInbox.DeferredName);
            if (File.Exists(senderDeferred))
                Assert.DoesNotContain("after the lock", File.ReadAllText(senderDeferred));
        }

        Assert.True(accepted);
        Assert.Null(error);

        var restarted = Open(dir, TimeSpan.FromMilliseconds(80));
        var filed = Assert.Single(restarted.InboxFor("dot").Pending());
        Assert.Equal("srv:carry", filed.Event.SourceId);
        Assert.Equal(1, filed.Event.Hop);
        Assert.True(filed.Event.Fanout);
        Assert.Equal("srv:cause", filed.Event.Parent);
        Assert.Equal("srv:cause", filed.Event.Root);
        Assert.Contains("after the lock", File.ReadAllText(Path.Combine(dir.Path, "agents", "dot", "room.jsonl")));
        Assert.Equal(RouteKind.Duplicate, restarted.Route(message).Kind);
        Assert.Single(restarted.InboxFor("dot").Pending());
    }

    [Fact]
    public void Unknown_parent_fanout_does_not_start_a_new_root()
    {
        using var dir = new TempDir();
        var router = Open(dir);
        var unknown = new RoomMessage("room", "dot", null,
            """{"fanout":true,"parent":"unknown-parent","text":"@muse one"}""", 1, "a1");
        Assert.Equal(RouteKind.Unresolved, router.Route(unknown).Kind);
        Assert.Equal(RouteKind.Unresolved, router.Route(new RoomMessage("room", "muse", null,
            """{"fanout":true,"parent":"unknown-parent","text":"@dot two"}""", 2, "a2")).Kind);
        Assert.Equal(RouteKind.Unresolved, router.Route(new RoomMessage("room", "dot", null,
            """{"fanout":true,"parent":"unknown-parent","text":"@muse three"}""", 3, "a3")).Kind);
        Assert.False(File.Exists(router.InboxFor("muse").InboxPath));
        Assert.False(File.Exists(router.InboxFor("dot").InboxPath));

        var restarted = Open(dir);
        Assert.Equal(RouteKind.Unresolved, restarted.Route(unknown).Kind);
        Assert.False(File.Exists(restarted.InboxFor("muse").InboxPath));

        var task = restarted.Route(new RoomMessage("room", "Alex", null, "@muse task", 4, "task"));
        Assert.Equal("srv:task", Assert.Single(task.Delivered).Root);

        Assert.Equal(RouteKind.Unresolved, restarted.Route(new RoomMessage("room", "muse", null,
            """{"fanout":true,"parent":"srv:task","root":"other-root","text":"@dot drift"}""", 5, "drift")).Kind);
        Assert.Equal(RouteKind.Unresolved, restarted.Route(new RoomMessage("room", "muse", null,
            """{"fanout":true,"root":"missing-root","text":"@dot ghost"}""", 6, "ghost")).Kind);
        Assert.False(File.Exists(restarted.InboxFor("dot").InboxPath));

        var byRoot = restarted.Route(new RoomMessage("room", "muse", null,
            """{"fanout":true,"root":"srv:task","text":"@dot by root"}""", 7, "by-root"));
        var rooted = Assert.Single(byRoot.Delivered);
        Assert.Equal(1, rooted.Hop);
        Assert.Null(rooted.Parent);
        Assert.Equal("srv:task", rooted.Root);
        Assert.NotEqual(rooted.SourceId, rooted.Root);

        Assert.Equal(RouteKind.Unresolved, restarted.Route(new RoomMessage("room", "muse", null,
            """{"fanout":true,"parent":"unknown-parent","text":"@dot bypass"}""", 8, "bypass")).Kind);
        Assert.Single(restarted.InboxFor("dot").Pending());

        Assert.Equal(RouteKind.HopLimited, restarted.Route(new RoomMessage("room", "dot", null, "!fanout @muse back", 9, "back")).Kind);
        Assert.Equal(RouteKind.Unresolved, restarted.Route(new RoomMessage("room", "dot", null,
            """{"fanout":true,"parent":"unknown-parent","text":"@muse also"}""", 10, "also")).Kind);
        Assert.Single(restarted.InboxFor("muse").Pending());
        Assert.Single(restarted.InboxFor("dot").Pending());
    }

    [Fact]
    public void Deferred_inbox_is_flushed_before_the_journal_is_cleared()
    {
        using var dir = new TempDir();
        var inbox = AgentInbox.Open(Path.Combine(dir.Path, "dot"), TimeSpan.FromMilliseconds(80));
        var deferred = Path.Combine(dir.Path, "dot", AgentInbox.DeferredName);
        var draft = Draft("dot:flush", "flush", "flush-me");
        using (new FileStream(Path.Combine(dir.Path, "dot", "inbox.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var write = inbox.Write(draft);
            Assert.Equal(InboxWriteKind.Deferred, write.Kind);
            Assert.Contains("flush-me", File.ReadAllText(deferred));
        }

        var flushedWhileDeferred = false;
        AgentInbox.OnDurableFlush = path =>
        {
            if (!string.Equals(path, inbox.InboxPath, StringComparison.Ordinal))
                return;
            Assert.Contains("flush-me", File.ReadAllText(inbox.InboxPath));
            Assert.Contains("flush-me", File.ReadAllText(deferred));
            flushedWhileDeferred = true;
        };
        try
        {
            var filed = Assert.Single(inbox.Pending());
            Assert.Equal("flush", filed.Event.SourceId);
        }
        finally
        {
            AgentInbox.OnDurableFlush = null;
        }

        Assert.True(flushedWhileDeferred);
        Assert.Equal(0, new FileInfo(deferred).Length);
        Assert.Contains("flush-me", File.ReadAllText(inbox.InboxPath));
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
