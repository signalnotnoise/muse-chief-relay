namespace Chief.Bridge.Tests;

public class AddressingTests
{
    [Theory]
    [InlineData("hey chief")]
    [InlineData("@chief can you look?")]
    [InlineData("Chief, status?")]
    [InlineData("CHIEF")]
    [InlineData("is chief's PR ready")]
    [InlineData("thanks chief.")]
    [InlineData("chief: ping")]
    public void Nick_as_a_whole_word_is_a_mention(string text)
    {
        var t = Addressing.Detect(text, "chief");
        Assert.NotNull(t);
        Assert.False(t!.IsTask);
    }

    [Theory]
    [InlineData("chiefly harmless")]
    [InlineData("mischief managed")]
    [InlineData("the Chief.Bridge build is green")]
    [InlineData("the chief-side bridge")]
    [InlineData("mail foo@chief")]
    [InlineData("hello Fuse")]
    [InlineData("")]
    [InlineData("   ")]
    public void Other_text_is_not_addressed(string text) => Assert.Null(Addressing.Detect(text, "chief"));

    [Fact]
    public void Json_task_to_the_nick_is_a_task_with_its_id()
    {
        var t = Addressing.Detect("""{"type":"task","id":"ten5fp3","to":"chief","title":"respon","body":"what are you doing"}""", "chief");
        Assert.Equal(new AckTrigger(true, "ten5fp3"), t);
        Assert.Equal(new AckTrigger(true, "42"), Addressing.Detect("""{"type":"task","id":42,"to":"Chief"}""", "chief"));
        Assert.Equal(new AckTrigger(true, null), Addressing.Detect("""{"type":"task","to":"chief"}""", "chief"));
    }

    [Theory]
    [InlineData("""{"type":"task","id":"x","to":"muse","body":"ask chief later"}""")]
    [InlineData("""{"type":"ack","id":"x","from":"chief"}""")]
    [InlineData("""{"type":"result","id":"x","from":"muse","summary":"chief was right"}""")]
    [InlineData("""{"type":"opinion","from":"muse","text":"chief is slow"}""")]
    [InlineData("""{"type":"ping"}""")]
    public void Other_protocol_lines_never_count_even_when_they_mention_the_nick(string text) =>
        Assert.Null(Addressing.Detect(text, "chief"));

    [Fact]
    public void Json_without_a_type_falls_back_to_the_text_rules() =>
        Assert.Equal(new AckTrigger(false, null), Addressing.Detect("""{"note":"chief"} hi chief""", "chief"));

    [Theory]
    [InlineData("TASK to chief: check PRs — list them")]
    [InlineData("task to @chief: x")]
    public void Shortcut_task_is_a_task_without_id(string text) =>
        Assert.Equal(new AckTrigger(true, null), Addressing.Detect(text, "chief"));

    [Fact]
    public void Nick_is_matched_literally_not_as_a_regex() =>
        Assert.Null(Addressing.Detect("hello cxhief", "c.hief"));
}

public class AutoAckerTests
{
    private const string Alex = "/Ab12+";
    private const string Fuse = "Xy34Zw";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_790_500_000);

    private static AutoAckConfig Cfg(Action<AutoAckConfig>? tweak = null)
    {
        var c = new AutoAckConfig { Enabled = true, MentionTrips = ["!" + Alex], TaskTrips = [Fuse] };
        tweak?.Invoke(c);
        c.Validate("test.json");
        return c;
    }

    private static HookView View(HookState s) => new(s, s.ToString(), null);
    private static readonly Func<HookView> Armed = () => View(HookState.Running);

    [Fact]
    public void Trusted_human_mention_is_acked_with_the_default_text()
    {
        var a = new AutoAcker(Cfg(), "chief");
        var d = a.Consider("Alex", Alex, "hey chief, you there?", T0, Armed);
        Assert.True(d.Send);
        Assert.Equal(new AutoAckConfig().Text, d.Text);
    }

    [Fact]
    public void Task_gets_the_task_text_with_its_id()
    {
        var a = new AutoAcker(Cfg(), "chief");
        var d = a.Consider("Alex", Alex, """{"type":"task","id":"ten5fp3","to":"chief","body":"what are you doing"}""", T0, Armed);
        Assert.True(d.Send);
        Assert.Equal("(auto) got task ten5fp3, thinking…", d.Text);
    }

    [Theory]
    [InlineData("chief", Alex, "chief says hi")]      // own nick
    [InlineData("Chief", Alex, "hi chief")]           // own nick, other case
    [InlineData("Alex", null, "hi chief")]            // untripped, even with a trusted nick
    [InlineData("Alex", "", "hi chief")]
    [InlineData("Mallory", "Zz99Zz", "hi chief")]     // untrusted trip
    [InlineData("Alex", Alex, "hi Fuse")]             // not addressed
    [InlineData("Fuse", Fuse, "hey chief, nice work")] // agent chatter: task-only trip
    public void These_are_never_acked(string nick, string? trip, string text)
    {
        var d = new AutoAcker(Cfg(), "chief").Consider(nick, trip, text, T0, Armed);
        Assert.False(d.Send);
        Assert.False(d.Suppressed);
    }

    [Fact]
    public void Task_only_trip_is_acked_for_a_task()
    {
        var d = new AutoAcker(Cfg(), "chief").Consider("Fuse", Fuse, """{"type":"task","id":"f1","to":"chief"}""", T0, Armed);
        Assert.True(d.Send);
    }

    [Fact]
    public void Own_trip_is_never_acked_even_if_it_is_on_a_list()
    {
        var a = new AutoAcker(Cfg(c => c.MentionTrips.Add("Q9a3Px")), "chief") { OwnTrip = "Q9a3Px" };
        Assert.False(a.Consider("chief2", "Q9a3Px", "chief?", T0, Armed).Send);
    }

    [Fact]
    public void Disabled_never_acks_and_never_reads_the_listener()
    {
        var a = new AutoAcker(new AutoAckConfig { MentionTrips = [Alex] }, "chief");
        var d = a.Consider("Alex", Alex, "chief?", T0, () => throw new InvalidOperationException());
        Assert.False(d.Send);
    }

    [Fact]
    public void Cooldown_holds_back_a_second_ack_then_releases()
    {
        var a = new AutoAcker(Cfg(c => c.CooldownSeconds = 60), "chief");
        Assert.True(a.Consider("Alex", Alex, "chief?", T0, Armed).Send);

        var held = a.Consider("Alex", Alex, "chief??", T0.AddSeconds(59), Armed);
        Assert.False(held.Send);
        Assert.True(held.Suppressed);
        Assert.Equal("cooldown", held.Reason);

        // Cooldown is global: a task from the other trusted trip is held too.
        Assert.False(a.Consider("Fuse", Fuse, """{"type":"task","to":"chief"}""", T0.AddSeconds(30), Armed).Send);
        Assert.True(a.Consider("Alex", Alex, "chief???", T0.AddSeconds(60), Armed).Send);
    }

    [Fact]
    public void Hourly_cap_limits_acks_in_a_rolling_hour()
    {
        var a = new AutoAcker(Cfg(c => { c.CooldownSeconds = 10; c.MaxPerHour = 3; }), "chief");
        var sent = Enumerable.Range(0, 6).Count(i => a.Consider("Alex", Alex, "chief?", T0.AddSeconds(i * 10), Armed).Send);
        Assert.Equal(3, sent);
        var capped = a.Consider("Alex", Alex, "chief?", T0.AddSeconds(100), Armed);
        Assert.Equal("hourly cap", capped.Reason);
        Assert.True(a.Consider("Alex", Alex, "chief?", T0.AddSeconds(3600), Armed).Send); // first one aged out
    }

    [Theory]
    [InlineData("Running", false)]
    [InlineData("NotConfigured", false)]
    [InlineData("Unknown", false)]
    [InlineData("NotRunning", true)]
    [InlineData("Failing", true)]
    public void Offline_text_only_when_the_hook_is_not_running_or_failing(string stateName, bool offline)
    {
        var state = Enum.Parse<HookState>(stateName);
        var d = new AutoAcker(Cfg(), "chief").Consider("Alex", Alex, "chief?", T0, () => View(state));
        Assert.True(d.Send);
        Assert.Equal(offline, d.Text == new AutoAckConfig().OfflineText);
    }

    [Fact]
    public void Placeholders_are_filled_with_cleaned_chat_values()
    {
        var a = new AutoAcker(Cfg(c => c.TaskText = "@{from} ack {id}"), "chief");
        var id = "a\nb" + new string('x', 60);
        var d = a.Consider("Alex", Alex, new System.Text.Json.Nodes.JsonObject { ["type"] = "task", ["id"] = id, ["to"] = "chief" }.ToJsonString(), T0, Armed);
        Assert.True(d.Send);
        Assert.DoesNotContain("\n", d.Text);
        Assert.StartsWith("@Alex ack ab", d.Text);
        Assert.EndsWith("…", d.Text);
    }
}

public class AutoAckConfigTests
{
    private static RelayConfig Load(string json)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), json);
        return RelayConfig.Load(dir.File("config.json"), allowExampleFallback: false);
    }

    [Fact]
    public void Absent_block_means_off()
    {
        var c = Load("""{"channel":"c","nick":"chief"}""");
        Assert.False(c.AutoAck.Enabled);
        Assert.Null(c.HookStatusPath());
    }

    [Fact]
    public void Snake_case_fields_are_read_and_trips_normalised()
    {
        var c = Load("""
            {"channel":"c","nick":"chief","auto_ack":{"enabled":true,"mention_trips":[" !/Ab12+ ",""],
             "task_trips":["Xy34Zw","Xy34Zw"],"cooldown_s":90,"max_per_hour":5,"text":"on it","task_text":"t {id}",
             "offline_text":"off"}}
            """);
        var a = c.AutoAck;
        Assert.True(a.Enabled);
        Assert.Equal(new[] { "/Ab12+" }, a.MentionTrips);
        Assert.Equal(new[] { "Xy34Zw" }, a.TaskTrips);
        Assert.Equal(90, a.CooldownSeconds);
        Assert.Equal(5, a.MaxPerHour);
        Assert.Equal(("on it", "t {id}", "off"), (a.Text, a.TaskText, a.OfflineText));
    }

    [Theory]
    [InlineData("""{"enabled":true}""")]
    [InlineData("""{"enabled":true,"mention_trips":["!"]}""")]
    [InlineData("""{"enabled":true,"mention_trips":["a"],"cooldown_s":5}""")]
    [InlineData("""{"enabled":true,"mention_trips":["a"],"max_per_hour":0}""")]
    [InlineData("""{"enabled":true,"mention_trips":["a"],"text":""}""")]
    public void Unsafe_enabled_configs_are_rejected(string block) =>
        Assert.Throws<ConfigException>(() => Load($$"""{"channel":"c","nick":"chief","auto_ack":{{block}}}"""));

    [Fact]
    public void A_disabled_block_is_not_validated_beyond_trips() =>
        Assert.False(Load("""{"channel":"c","nick":"chief","auto_ack":{"enabled":false,"cooldown_s":1}}""").AutoAck.Enabled);
}
