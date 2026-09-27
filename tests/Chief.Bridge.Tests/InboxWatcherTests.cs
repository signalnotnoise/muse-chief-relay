using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

public class InboxWatcherTests
{
    private static string Chat(string nick, string text, string? trip = null, long ts = 1790468200, string dir = "in")
    {
        var msg = new JsonObject { ["cmd"] = "chat", ["nick"] = nick, ["text"] = text, ["channel"] = "c" };
        if (trip is not null) msg["trip"] = trip;
        return new JsonObject { ["ts"] = ts, ["dir"] = dir, ["msg"] = msg }.ToJsonString(JsonUtil.Opts) + "\n";
    }

    private static string Frame(string cmd, string dir = "in") =>
        new JsonObject { ["ts"] = 1, ["dir"] = dir, ["msg"] = new JsonObject { ["cmd"] = cmd, ["nick"] = "x" } }
            .ToJsonString(JsonUtil.Opts) + "\n";

    private static (TempDir dir, string inbox, InboxWatcher w) Setup(string nick = "chief", TimeSpan? poll = null)
    {
        var dir = new TempDir();
        var inbox = dir.File("inbox.jsonl");
        return (dir, inbox, new InboxWatcher(inbox, dir.File(".inbox_watch.offset"), nick, poll ?? TimeSpan.FromMilliseconds(100)));
    }

    private static IReadOnlyList<WatchedChat> PollCommit(InboxWatcher w)
    {
        var p = w.Poll();
        w.Commit(p);
        return p.Chats;
    }

    [Fact]
    public void First_run_bootstraps_silently_then_reports_only_new_chats()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "old history") + Chat("Fuse", "more history"));

        var first = w.Poll();
        Assert.True(first.Bootstrapped);
        Assert.Empty(first.Chats);
        w.Commit(first);
        Assert.True(File.Exists(dir.File(".inbox_watch.offset")));

        File.AppendAllText(inbox, Chat("Alex", "hello", trip: "AbCdEf", ts: 1790468300));
        var chats = PollCommit(w);

        var c = Assert.Single(chats);
        Assert.Equal("Alex", c.Nick);
        Assert.Equal("AbCdEf", c.Trip);
        Assert.Equal("hello", c.Text);
        Assert.Equal(1790468300, c.Ts!.GetValue<long>());
        Assert.Empty(PollCommit(w));
    }

    [Fact]
    public void Output_is_a_json_array_of_nick_trip_text_ts()
    {
        var json = WatchedChat.ToJsonArray(new[] { new WatchedChat("Alex", null, "it's café ✓", JsonValue.Create(5L)) });
        Assert.Equal("[{\"nick\":\"Alex\",\"trip\":null,\"text\":\"it's café ✓\",\"ts\":5}]", json);
        Assert.Equal("[]", WatchedChat.ToJsonArray(Array.Empty<WatchedChat>()));
    }

    [Fact]
    public void Own_nick_is_skipped_and_nick_override_is_honoured()
    {
        var (dir, inbox, w) = Setup("chief");
        using var _ = dir;
        File.WriteAllText(inbox, "");
        PollCommit(w);
        File.AppendAllText(inbox, Chat("chief", "my own echo") + Chat("Fuse", "hi chief"));

        Assert.Equal(new[] { "Fuse" }, PollCommit(w).Select(c => c.Nick));

        var other = new InboxWatcher(inbox, dir.File("other.offset"), "Fuse");
        File.WriteAllText(dir.File("other.offset"), "0");
        Assert.Equal(new[] { "chief" }, PollCommit(other).Select(c => c.Nick));
    }

    [Fact]
    public void Non_chat_and_outbound_frames_are_skipped()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, "");
        PollCommit(w);
        File.AppendAllText(inbox,
            Frame("onlineAdd") + Frame("onlineSet") + Frame("warn") + Frame("info") +
            Chat("Alex", "outbound copy", dir: "out") +
            "{\"ts\":1,\"dir\":\"err\",\"msg\":{\"error\":\"x\"}}\n" +
            Chat("Alex", "the real one"));

        Assert.Equal(new[] { "the real one" }, PollCommit(w).Select(c => c.Text));
    }

    [Fact]
    public void Malformed_and_blank_lines_are_skipped_without_blocking_progress()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, "");
        PollCommit(w);
        File.AppendAllText(inbox,
            "not json\n\n   \n[1,2]\n{\"dir\":\"in\",\"msg\":\"string\"}\n{\"dir\":\"in\",\"msg\":{\"cmd\":7}}\n{truncated\n" +
            Chat("Alex", "after the junk"));

        Assert.Equal(new[] { "after the junk" }, PollCommit(w).Select(c => c.Text));
        Assert.Empty(PollCommit(w));
    }

    [Fact]
    public void Partial_line_is_not_consumed_until_its_newline_arrives()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, "");
        PollCommit(w);

        var line = Chat("Alex", "written in two parts");
        File.AppendAllText(inbox, line[..20]);
        Assert.Empty(PollCommit(w));

        File.AppendAllText(inbox, line[20..]);
        Assert.Equal(new[] { "written in two parts" }, PollCommit(w).Select(c => c.Text));
    }

    [Fact]
    public void Bootstrap_stops_at_the_last_complete_line()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        var line = Chat("Alex", "in flight at bootstrap");
        File.WriteAllText(inbox, Chat("Alex", "history") + line[..15]);
        PollCommit(w);

        File.AppendAllText(inbox, line[15..]);
        Assert.Equal(new[] { "in flight at bootstrap" }, PollCommit(w).Select(c => c.Text));
    }

    [Fact]
    public void Truncated_inbox_resets_the_offset()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "a long line of history that makes the file big") + Chat("Alex", "more"));
        PollCommit(w);

        File.WriteAllText(inbox, Chat("Fuse", "after truncation"));
        var p = w.Poll();

        Assert.True(p.Reset);
        Assert.Equal(new[] { "after truncation" }, p.Chats.Select(c => c.Text));
    }

    [Fact]
    public void Rotated_inbox_that_already_outgrew_the_offset_is_still_detected()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "old"));
        PollCommit(w);

        File.Delete(inbox);
        File.WriteAllText(inbox, Chat("Fuse", "new file, first line") + Chat("Fuse", "new file, second line"));
        var p = w.Poll();

        Assert.True(p.Reset);
        Assert.Equal(2, p.Chats.Count);
    }

    [Fact]
    public void Rotation_check_covers_every_consumed_byte_while_the_offset_is_under_256()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        var first = Chat("Alex", "aaaa");
        Assert.True(first.Length < InboxWatcher.HeadBytes);
        File.WriteAllText(inbox, first);
        PollCommit(w);

        // Growing under 256 bytes is not mistaken for a rotation: the hash never covers bytes past the offset.
        File.AppendAllText(inbox, Chat("Fuse", "grow"));
        var grown = w.Poll();
        Assert.False(grown.Reset);
        Assert.Equal(new[] { "grow" }, grown.Chats.Select(c => c.Text));
        w.Commit(grown);

        // A replacement that differs anywhere in the consumed bytes (here the last byte before the offset's
        // final newline) is caught, even though the file is the same length and the first line is untouched.
        var replaced = first + Chat("Fuse", "grox") + Chat("Fuse", "new");
        File.WriteAllText(inbox, replaced);
        var p = w.Poll();
        Assert.True(p.Reset);
        Assert.Equal(new[] { "aaaa", "grox", "new" }, p.Chats.Select(c => c.Text));
    }

    [Fact]
    public void Missing_inbox_prints_nothing_and_counts_everything_once_it_appears()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        Assert.Empty(PollCommit(w));

        File.WriteAllText(inbox, Chat("Alex", "first ever line"));
        Assert.Equal(new[] { "first ever line" }, PollCommit(w).Select(c => c.Text));
    }

    [Fact]
    public void Offset_file_is_json_bytes_and_written_atomically()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "x"));
        PollCommit(w);

        var saved = JsonNode.Parse(File.ReadAllText(dir.File(".inbox_watch.offset")))!;
        Assert.Equal(new FileInfo(inbox).Length, saved["offset"]!.GetValue<long>());
        Assert.Equal(64, saved["head"]!.GetValue<string>().Length);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp.*"));
    }

    [Fact]
    public void Unreadable_offset_file_rebootstraps_instead_of_replaying_history()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "history"));
        File.WriteAllText(dir.File(".inbox_watch.offset"), "garbage");

        var p = w.Poll();

        Assert.True(p.Bootstrapped);
        Assert.Empty(p.Chats);
        Assert.NotNull(w.Warning);
    }

    [Theory]
    [InlineData("{\"offset\":0}")]
    [InlineData("{\"offset\":0,\"head\":null}")]
    [InlineData("{\"offset\":0,\"head\":42}")]
    [InlineData("{\"offset\":0,\"head\":\"not-a-hash\"}")]
    [InlineData("{\"offset\":-1,\"head\":\"0000000000000000000000000000000000000000000000000000000000000000\"}")]
    public void Json_offset_without_a_valid_head_is_corrupt_and_rebootstraps(string content)
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "history"));
        File.WriteAllText(dir.File(".inbox_watch.offset"), content);

        var p = w.Poll();

        Assert.True(p.Bootstrapped);
        Assert.Empty(p.Chats);
        Assert.NotNull(w.Warning);
        w.Commit(p);
        var saved = JsonNode.Parse(File.ReadAllText(dir.File(".inbox_watch.offset")))!;
        Assert.Equal(new FileInfo(inbox).Length, saved["offset"]!.GetValue<long>());
        Assert.Equal(64, saved["head"]!.GetValue<string>().Length);
    }

    [Fact]
    public void Bare_number_offset_is_still_accepted()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "from zero"));
        File.WriteAllText(dir.File(".inbox_watch.offset"), "0\n");

        Assert.Equal(new[] { "from zero" }, PollCommit(w).Select(c => c.Text));
        Assert.Null(w.Warning);
    }

    [Fact]
    public async Task Wait_keeps_the_offset_warning_after_later_polls()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "history"));
        File.WriteAllText(dir.File(".inbox_watch.offset"), "garbage");

        var task = w.WaitAsync(TimeSpan.FromSeconds(10), _ => { }, CancellationToken.None);
        await Task.Delay(400); // several polls read the rewritten, valid offset file
        File.AppendAllText(inbox, Chat("Fuse", "wake up"));

        Assert.Equal(WaitOutcome.Delivered, await task);
        Assert.NotNull(w.Warning);
    }

    [Fact]
    public async Task Wait_keeps_the_offset_warning_on_timeout()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "history"));
        File.WriteAllText(dir.File(".inbox_watch.offset"), "{\"offset\":0}");

        Assert.Equal(WaitOutcome.TimedOut, await w.WaitAsync(TimeSpan.FromMilliseconds(350), _ => { }, CancellationToken.None));
        Assert.NotNull(w.Warning);
    }

    [Fact]
    public async Task Wait_with_the_largest_timeout_does_not_overflow()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, "");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var max = WatchOptions.Parse(["--wait", "--timeout", WatchOptions.MaxTimeoutSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)]).Timeout;

        Assert.Equal(WaitOutcome.Cancelled, await w.WaitAsync(max, _ => { }, cts.Token));
        Assert.Equal(WaitOutcome.Cancelled, await w.WaitAsync(TimeSpan.MaxValue, _ => { }, new CancellationToken(true)));
    }

    [Fact]
    public async Task Wait_returns_when_a_new_chat_arrives()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "history"));
        IReadOnlyList<WatchedChat>? got = null;

        var sw = Stopwatch.StartNew();
        var task = w.WaitAsync(TimeSpan.FromSeconds(10), c => got = c, CancellationToken.None);
        await Task.Delay(400);
        Assert.False(task.IsCompleted);
        File.AppendAllText(inbox, Chat("Fuse", "wake up"));

        Assert.Equal(WaitOutcome.Delivered, await task);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "wake up" }, got!.Select(c => c.Text));
        Assert.Empty(PollCommit(w)); // offset advanced past it
    }

    [Fact]
    public async Task Wait_is_not_woken_by_own_nick_or_non_chat_frames()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, "");
        IReadOnlyList<WatchedChat>? got = null;

        var task = w.WaitAsync(TimeSpan.FromSeconds(10), c => got = c, CancellationToken.None);
        await Task.Delay(200);
        File.AppendAllText(inbox, Chat("chief", "my own say echo") + Frame("onlineAdd"));
        await Task.Delay(500);
        Assert.False(task.IsCompleted);

        File.AppendAllText(inbox, Chat("Alex", "a real message"));
        Assert.Equal(WaitOutcome.Delivered, await task);
        Assert.Equal(new[] { "a real message" }, got!.Select(c => c.Text));
    }

    [Fact]
    public async Task Wait_times_out_with_nothing_new()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, Chat("Alex", "history"));
        var delivered = false;

        var sw = Stopwatch.StartNew();
        var outcome = await w.WaitAsync(TimeSpan.FromSeconds(1.2), _ => delivered = true, CancellationToken.None);

        Assert.Equal(WaitOutcome.TimedOut, outcome);
        Assert.False(delivered);
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.1, 5);
    }

    [Fact]
    public async Task Wait_stops_on_cancellation()
    {
        var (dir, inbox, w) = Setup();
        using var _ = dir;
        File.WriteAllText(inbox, "");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Assert.Equal(WaitOutcome.Cancelled, await w.WaitAsync(null, _ => { }, cts.Token));
    }

    /// <summary>Injects transient poll failures: the first <c>failures</c> polls throw.</summary>
    private sealed class FlakyWatcher : InboxWatcher
    {
        private int _failuresLeft;

        public FlakyWatcher(string inbox, string offset, string nick, int failures)
            : base(inbox, offset, nick, TimeSpan.FromMilliseconds(50)) =>
            _failuresLeft = failures;

        public override WatchPoll Poll() =>
            _failuresLeft-- > 0 ? throw new IOException("simulated torn read") : base.Poll();
    }

    [Fact]
    public void TryPoll_reports_failure_instead_of_throwing()
    {
        var dir = new TempDir();
        using var _ = dir;
        var w = new FlakyWatcher(dir.File("inbox.jsonl"), dir.File(".inbox_watch.offset"), "chief", failures: 1);

        Assert.False(w.TryPoll(out var poll, out var error));
        Assert.Null(poll);
        Assert.Contains("simulated torn read", error);

        Assert.True(w.TryPoll(out poll, out error));
        Assert.NotNull(poll);
        Assert.Null(error);
    }

    [Fact]
    public async Task Wait_survives_transient_poll_failures_then_delivers()
    {
        var dir = new TempDir();
        using var _ = dir;
        var inbox = dir.File("inbox.jsonl");
        var w = new FlakyWatcher(inbox, dir.File(".inbox_watch.offset"), "chief", failures: 3);
        File.WriteAllText(inbox, Chat("Alex", "history"));
        IReadOnlyList<WatchedChat>? got = null;

        var task = w.WaitAsync(TimeSpan.FromSeconds(10), c => got = c, CancellationToken.None);
        await Task.Delay(500); // several polls fail, then the watcher bootstraps and keeps waiting
        Assert.False(task.IsCompleted);
        File.AppendAllText(inbox, Chat("Fuse", "wake up"));

        Assert.Equal(WaitOutcome.Delivered, await task);
        Assert.Equal(new[] { "wake up" }, got!.Select(c => c.Text));
        Assert.Contains("inbox poll failed", w.Warning);
        Assert.Empty(PollCommit(w)); // offset advanced past the delivered chat
    }

    [Fact]
    public async Task Wait_with_settle_survives_a_poll_failure_mid_burst()
    {
        var dir = new TempDir();
        using var _ = dir;
        var inbox = dir.File("inbox.jsonl");
        // One failure for the initial wait loop, one for inside the settle window.
        var w = new FlakyWatcher(inbox, dir.File(".inbox_watch.offset"), "chief", failures: 2);
        File.WriteAllText(inbox, Chat("Alex", "history"));
        IReadOnlyList<WatchedChat>? got = null;

        var task = w.WaitAsync(TimeSpan.FromSeconds(15), c => got = c, CancellationToken.None,
            settle: TimeSpan.FromSeconds(3));
        await Task.Delay(500); // first failure spent, watcher bootstrapped and waiting
        File.AppendAllText(inbox, Chat("Fuse", "part one"));
        await Task.Delay(700); // settle is collecting; its first re-poll fails, then recovers
        File.AppendAllText(inbox, Chat("Fuse", "part two"));

        Assert.Equal(WaitOutcome.Delivered, await task);
        Assert.Equal(new[] { "part one", "part two" }, got!.Select(c => c.Text));
        Assert.Contains("inbox poll failed", w.Warning);
    }

    [Fact]
    public async Task Wait_times_out_when_every_poll_fails()
    {
        var dir = new TempDir();
        using var _ = dir;
        var w = new FlakyWatcher(dir.File("inbox.jsonl"), dir.File(".inbox_watch.offset"), "chief",
            failures: int.MaxValue);

        var outcome = await w.WaitAsync(TimeSpan.FromMilliseconds(400), _ => Assert.Fail("must not deliver"),
            CancellationToken.None);

        Assert.Equal(WaitOutcome.TimedOut, outcome);
        Assert.Contains("inbox poll failed", w.Warning);
    }
}

public class WatchOptionsTests
{
    [Fact]
    public void Defaults() => Assert.Equal(new WatchOptions(null, null, false, null), WatchOptions.Parse([]));

    [Fact]
    public void All_options_in_both_forms()
    {
        var o = WatchOptions.Parse(["--nick", "chief", "--state=/tmp/o", "--wait", "--timeout", "2.5"]);
        Assert.Equal(new WatchOptions("chief", "/tmp/o", true, TimeSpan.FromSeconds(2.5)), o);
    }

    [Theory]
    [InlineData("--timeout", "5")]
    [InlineData("--wait", "--timeout", "-1")]
    [InlineData("--wait", "--timeout", "soon")]
    [InlineData("--wait", "--timeout", "NaN")]
    [InlineData("--wait", "--timeout", "Infinity")]
    [InlineData("--wait", "--timeout", "1e308")]
    [InlineData("--wait", "--timeout=922337203686")]
    [InlineData("--nick")]
    [InlineData("--nick=")]
    [InlineData("--bogus")]
    public void Bad_options_are_errors(params string[] args) =>
        Assert.Throws<ArgumentException>(() => WatchOptions.Parse(args));

    [Fact]
    public void Watch_is_a_subcommand_and_keeps_its_options()
    {
        var c = CliArgs.Parse(["watch", "--config", "/c.json", "--wait", "--timeout", "3"]);
        Assert.Equal("watch", c.Command);
        Assert.Equal("/c.json", c.ConfigPath);
        Assert.Equal(new[] { "--wait", "--timeout", "3" }, c.Rest);
    }
}
