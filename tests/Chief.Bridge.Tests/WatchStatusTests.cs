using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

public class WatchStatusTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_790_500_000);
    private static readonly Func<int, bool> Alive = _ => true;
    private static readonly Func<int, bool> Dead = _ => false;

    private static WatchStatus S(string state, long hbAgo = 0, long? exitedAgo = null, long? deadlineIn = null) =>
        new(1234, state, T0.ToUnixTimeSeconds() - 600, T0.ToUnixTimeSeconds() - hbAgo,
            deadlineIn is { } d ? T0.ToUnixTimeSeconds() + d : null,
            exitedAgo is { } e ? T0.ToUnixTimeSeconds() - e : null, null, null, 3);

    [Fact]
    public void Status_file_sits_next_to_the_offset_file() =>
        Assert.Equal("/b/.inbox_watch.offset.status", WatchStatus.PathFor("/b/.inbox_watch.offset"));

    [Fact]
    public void Writer_and_reader_round_trip_through_every_phase_without_leaving_temp_files()
    {
        using var dir = new TempDir();
        var path = dir.File(".inbox_watch.offset.status");
        var now = T0;
        var w = new WatchStatusWriter(path, () => now);

        w.Armed(TimeSpan.FromSeconds(1800), 3);
        var armed = WatchStatus.TryRead(path)!;
        Assert.Equal(("armed", Environment.ProcessId, T0.ToUnixTimeSeconds() + 1800, 3.0),
            (armed.State, armed.Pid, armed.Deadline!.Value, armed.SettleSeconds));
        Assert.Null(armed.ExitedAt);

        now = T0.AddSeconds(40);
        w.Tick(WatchPhase.Settling); // phase change: written at once
        Assert.Equal("settling", WatchStatus.TryRead(path)!.State);

        now = T0.AddSeconds(42);
        w.Exited(WatchStatus.DeliveredState, 0, 2);
        var done = WatchStatus.TryRead(path)!;
        Assert.Equal(("delivered", 0, 2, T0.ToUnixTimeSeconds() + 42), (done.State, done.ExitCode!.Value, done.Delivered!.Value, done.ExitedAt!.Value));
        Assert.Equal(T0.ToUnixTimeSeconds(), done.ArmedAt);
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Heartbeat_is_throttled_but_a_phase_change_is_not()
    {
        using var dir = new TempDir();
        var path = dir.File("s");
        var now = T0;
        var w = new WatchStatusWriter(path, () => now, heartbeatInterval: TimeSpan.FromHours(1));
        w.Armed(null, 0);
        Assert.Null(WatchStatus.TryRead(path)!.Deadline);

        now = T0.AddSeconds(10);
        w.Tick(WatchPhase.Waiting);
        Assert.Equal(T0.ToUnixTimeSeconds(), WatchStatus.TryRead(path)!.HeartbeatAt); // not due yet

        w.Tick(WatchPhase.Settling);
        Assert.Equal(T0.ToUnixTimeSeconds() + 10, WatchStatus.TryRead(path)!.HeartbeatAt);

        var fast = new WatchStatusWriter(path, () => now, heartbeatInterval: TimeSpan.Zero);
        fast.Armed(null, 0);
        now = T0.AddSeconds(20);
        fast.Tick(WatchPhase.Waiting);
        Assert.Equal(T0.ToUnixTimeSeconds() + 20, WatchStatus.TryRead(path)!.HeartbeatAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"state":"armed"}""")]
    public void Unusable_status_files_read_as_missing(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s"), content);
        Assert.Null(WatchStatus.TryRead(dir.File("s")));
        Assert.Null(WatchStatus.TryRead(dir.File("missing")));
    }

    [Fact]
    public void No_status_is_unknown() =>
        Assert.Equal(ListenerState.Unknown, ListenerView.Classify(null, T0, Alive).State);

    [Fact]
    public void Live_heartbeating_watcher_is_armed()
    {
        var v = ListenerView.Classify(S("armed", hbAgo: 4, deadlineIn: 600), T0, Alive);
        Assert.Equal(ListenerState.Armed, v.State);
        Assert.Contains("times out in 10m", v.Detail);
        Assert.Equal(ListenerState.Armed, ListenerView.Classify(S("settling", hbAgo: 1), T0, Alive).State);
    }

    [Fact]
    public void Armed_but_dead_or_silent_is_not_armed()
    {
        var dead = ListenerView.Classify(S("armed", hbAgo: 4), T0, Dead);
        Assert.Equal(ListenerState.NotArmed, dead.State);
        Assert.Contains("died without a clean exit", dead.Detail);

        var stale = ListenerView.Classify(S("armed", hbAgo: 31), T0, Alive);
        Assert.Equal(ListenerState.NotArmed, stale.State);
        Assert.Contains("heartbeat", stale.Detail);
    }

    [Theory]
    [InlineData("delivered", 30, "Waking")]
    [InlineData("timed_out", 30, "Waking")]
    [InlineData("polled", 5, "Waking")]
    [InlineData("delivered", 181, "NotArmed")]
    [InlineData("timed_out", 2400, "NotArmed")] // tonight's failure: a quiet timeout nobody re-armed
    [InlineData("stopped", 1, "NotArmed")]
    public void Exited_watcher_is_waking_for_a_grace_period_then_not_armed(string state, long exitedAgo, string expectedName)
    {
        var expected = Enum.Parse<ListenerState>(expectedName);
        var v = ListenerView.Classify(S(state, hbAgo: exitedAgo, exitedAgo: exitedAgo), T0, Dead);
        Assert.Equal(expected, v.State);
        if (expected == ListenerState.NotArmed && state == "timed_out")
            Assert.Contains("NOT ARMED: last watch timed out", v.Detail);
    }

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(600, "10m")]
    [InlineData(7260, "2h01m")]
    public void Durations_read_naturally(int secs, string expected) =>
        Assert.Equal(expected, ListenerView.Human(TimeSpan.FromSeconds(secs)));
}

public class WatchSettleTests
{
    private static string Chat(string nick, string text) =>
        new JsonObject { ["ts"] = 1, ["dir"] = "in", ["msg"] = new JsonObject { ["cmd"] = "chat", ["nick"] = nick, ["text"] = text } }
            .ToJsonString(JsonUtil.Opts) + "\n";

    private static (TempDir dir, string inbox, InboxWatcher w) Setup()
    {
        var dir = new TempDir();
        var inbox = dir.File("inbox.jsonl");
        File.WriteAllText(inbox, "");
        var w = new InboxWatcher(inbox, dir.File(".inbox_watch.offset"), "chief", TimeSpan.FromMilliseconds(50));
        w.Commit(w.Poll());
        return (dir, inbox, w);
    }

    [Fact]
    public async Task A_burst_inside_the_settle_window_comes_back_as_one_delivery()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        IReadOnlyList<WatchedChat>? got = null;
        var calls = 0;

        var task = w.WaitAsync(TimeSpan.FromSeconds(10), c => { got = c; calls++; }, CancellationToken.None, TimeSpan.FromMilliseconds(800));
        await Task.Delay(150);
        File.AppendAllText(inbox, Chat("Alex", "Hello again"));
        await Task.Delay(300);
        Assert.False(task.IsCompleted); // still settling
        File.AppendAllText(inbox, Chat("chief", "own echo") + Chat("Alex", "and a task"));

        Assert.Equal(WaitOutcome.Delivered, await task);
        Assert.Equal(1, calls);
        Assert.Equal(new[] { "Hello again", "and a task" }, got!.Select(c => c.Text));
        Assert.Empty(w.Poll().Chats); // everything committed
    }

    [Fact]
    public async Task Settle_goes_quiet_then_returns()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        var sw = new Stopwatch();
        var task = w.WaitAsync(null, _ => { }, CancellationToken.None, TimeSpan.FromMilliseconds(400));
        await Task.Delay(100);
        sw.Start();
        File.AppendAllText(inbox, Chat("Alex", "one"));
        Assert.Equal(WaitOutcome.Delivered, await task);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 350, 3000);
    }

    [Fact]
    public async Task A_steady_stream_is_cut_off_at_the_cap()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                File.AppendAllText(inbox, Chat("Alex", $"msg {i++}"));
                await Task.Delay(100);
            }
        });

        var sw = Stopwatch.StartNew();
        IReadOnlyList<WatchedChat>? got = null;
        var outcome = await w.WaitAsync(null, c => got = c, CancellationToken.None, TimeSpan.FromMilliseconds(500));
        var elapsed = sw.Elapsed;
        stop.Cancel();
        await writer;

        Assert.Equal(WaitOutcome.Delivered, outcome);
        Assert.InRange(elapsed.TotalSeconds, 1.8, 4.0); // cap = 4 x 0.5 s, never the quiet window
        Assert.True(got!.Count > 5);
    }

    [Fact]
    public async Task Cancellation_while_settling_still_delivers_what_arrived()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        using var cts = new CancellationTokenSource();
        IReadOnlyList<WatchedChat>? got = null;
        var task = w.WaitAsync(null, c => got = c, cts.Token, TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        File.AppendAllText(inbox, Chat("Alex", "don't lose me"));
        await Task.Delay(300);
        cts.Cancel();

        Assert.Equal(WaitOutcome.Delivered, await task);
        Assert.Equal("don't lose me", Assert.Single(got!).Text);
        Assert.Empty(w.Poll().Chats);
    }

    [Fact]
    public async Task Tick_reports_waiting_then_settling()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        var phases = new List<WatchPhase>();
        var task = w.WaitAsync(null, _ => { }, CancellationToken.None, TimeSpan.FromMilliseconds(200), p => { lock (phases) phases.Add(p); });
        await Task.Delay(200);
        File.AppendAllText(inbox, Chat("Alex", "x"));
        await task;

        lock (phases)
        {
            Assert.Equal(WatchPhase.Waiting, phases[0]);
            Assert.Contains(WatchPhase.Settling, phases);
            Assert.Equal(WatchPhase.Settling, phases[^1]);
        }
    }

    [Fact]
    public void Settle_option_parses_and_is_bounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), WatchOptions.Parse(["--wait", "--settle", "3"]).Settle);
        Assert.Equal(TimeSpan.FromSeconds(0.5), WatchOptions.Parse(["--wait", "--settle=0.5"]).Settle);
        Assert.Null(WatchOptions.Parse(["--wait"]).Settle);
    }

    [Theory]
    [InlineData("--settle", "3")]
    [InlineData("--wait", "--settle", "-1")]
    [InlineData("--wait", "--settle", "61")]
    [InlineData("--wait", "--settle", "NaN")]
    [InlineData("--wait", "--settle")]
    public void Bad_settle_values_are_errors(params string[] args) =>
        Assert.Throws<ArgumentException>(() => WatchOptions.Parse(args));
}

/// <summary>End to end through Program.Main (in process), against a temp config. Nothing touches the network.</summary>
public class WatchCliTests
{
    private static (TempDir dir, string cfg) Deployment()
    {
        var dir = new TempDir();
        var cfg = dir.File("config.json");
        File.WriteAllText(cfg, """{"channel":"c","nick":"chief"}""");
        File.WriteAllText(dir.File("inbox.jsonl"), "");
        return (dir, cfg);
    }

    [Fact]
    public async Task Wait_timeout_leaves_a_timed_out_status_and_exits_3()
    {
        var (dir, cfg) = Deployment();
        using var _ = dir;
        Assert.Equal(0, await Program.Main(["watch", "--config", cfg])); // bootstrap
        var polled = WatchStatus.TryRead(dir.File(".inbox_watch.offset.status"))!;
        Assert.Equal("polled", polled.State);

        Assert.Equal(3, await Program.Main(["watch", "--config", cfg, "--wait", "--timeout", "0.3", "--settle", "1"]));
        var s = WatchStatus.TryRead(dir.File(".inbox_watch.offset.status"))!;
        Assert.Equal(("timed_out", 3, Environment.ProcessId, 1.0), (s.State, s.ExitCode!.Value, s.Pid, s.SettleSeconds));
        Assert.NotNull(s.ExitedAt);
    }

    [Fact]
    public async Task Wait_that_delivers_records_the_count_and_a_custom_state_keeps_its_own_status()
    {
        var (dir, cfg) = Deployment();
        using var _ = dir;
        var state = dir.File("sub/w.offset");
        Assert.Equal(0, await Program.Main(["watch", "--config", cfg, "--state", state]));

        var task = Program.Main(["watch", "--config", cfg, "--state", state, "--wait", "--timeout", "10"]);
        await Task.Delay(300);
        var armed = WatchStatus.TryRead(state + ".status")!;
        Assert.Equal("armed", armed.State);
        Assert.Equal(ListenerState.Armed, ListenerView.Classify(armed, DateTimeOffset.UtcNow, ProcessInfo.IsRunning).State);

        File.AppendAllText(dir.File("inbox.jsonl"),
            new JsonObject { ["ts"] = 1, ["dir"] = "in", ["msg"] = new JsonObject { ["cmd"] = "chat", ["nick"] = "Alex", ["text"] = "hi" } }
                .ToJsonString() + "\n");
        Assert.Equal(0, await task);
        var s = WatchStatus.TryRead(state + ".status")!;
        Assert.Equal(("delivered", 1), (s.State, s.Delivered!.Value));
        Assert.False(File.Exists(dir.File(".inbox_watch.offset.status"))); // default location untouched
    }

    [Fact]
    public async Task One_shot_watch_exits_2_when_the_inbox_path_is_a_directory()
    {
        var (dir, cfg) = Deployment();
        using var _ = dir;
        var inbox = dir.File("inbox.jsonl");
        File.Delete(inbox);
        Directory.CreateDirectory(inbox);

        Assert.Equal(2, await Program.Main(["watch", "--config", cfg]));
        Assert.False(File.Exists(dir.File(".inbox_watch.offset")));
    }

    [Fact]
    public async Task Status_accepts_state_and_rejects_unknown_arguments()
    {
        var (dir, cfg) = Deployment();
        using var _ = dir;
        Assert.Equal(0, await Program.Main(["status", "--config", cfg, "--state", dir.File("x.offset")]));
        Assert.Equal(0, await Program.Main(["status", "--config", cfg]));
        Assert.Equal(2, await Program.Main(["status", "--config", cfg, "--bogus"]));
    }
}
