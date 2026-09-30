namespace Chief.Bridge.Tests;

public class CliArgsTests
{
    [Fact]
    public void No_args_is_a_bridge_run_without_explicit_config()
    {
        var c = CliArgs.Parse([]);
        Assert.Equal("run", c.Command);
        Assert.Null(c.ConfigPath);
    }

    [Fact]
    public void Bare_argument_is_the_config_path_for_the_run() =>
        Assert.Equal("/x/config.json", CliArgs.Parse(["/x/config.json"]).ConfigPath);

    [Theory]
    [InlineData("say", "--config", "/c.json", "hello", "world")]
    [InlineData("--config", "/c.json", "say", "hello", "world")]
    [InlineData("say", "--config=/c.json", "hello", "world")]
    public void Say_accepts_config_before_or_after_the_subcommand(params string[] args)
    {
        var c = CliArgs.Parse(args);
        Assert.Equal("say", c.Command);
        Assert.Equal("/c.json", c.ConfigPath);
        Assert.Equal("hello world", string.Join(' ', c.Rest));
    }

    [Fact]
    public void Double_dash_makes_the_rest_literal()
    {
        var c = CliArgs.Parse(["say", "--", "--config", "is", "text"]);
        Assert.Null(c.ConfigPath);
        Assert.Equal("--config is text", string.Join(' ', c.Rest));
    }

    [Theory]
    [InlineData("--config")]
    [InlineData("--config=")]
    [InlineData("--config", "a", "--config", "b")]
    [InlineData("--config", "a", "b")]
    [InlineData("a", "b")]
    public void Bad_combinations_are_errors(params string[] args) =>
        Assert.Throws<ArgumentException>(() => CliArgs.Parse(args));
}

public class RelayConfigTests
{
    private const string Valid = "{\"channel\":\"c\",\"nick\":\"n\"}";
    private static string? NoEnv(string _) => null;

    [Fact]
    public void Explicit_path_wins_over_env_and_cwd()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("explicit.json"), "{\"channel\":\"explicit\",\"nick\":\"n\"}");
        File.WriteAllText(dir.File("env.json"), "{\"channel\":\"env\",\"nick\":\"n\"}");
        File.WriteAllText(dir.File("config.json"), "{\"channel\":\"cwd\",\"nick\":\"n\"}");

        var cfg = RelayConfig.Load(dir.File("explicit.json"), false, dir.Path, _ => dir.File("env.json"));

        Assert.Equal("explicit", cfg.Channel);
        Assert.Equal("command line", cfg.Source);
    }

    [Fact]
    public void Env_wins_over_cwd()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("env.json"), "{\"channel\":\"env\",\"nick\":\"n\"}");
        File.WriteAllText(dir.File("config.json"), "{\"channel\":\"cwd\",\"nick\":\"n\"}");

        Assert.Equal("env", RelayConfig.Load(null, false, dir.Path, _ => dir.File("env.json")).Channel);
    }

    [Fact]
    public void Missing_explicit_or_env_path_is_an_error_not_a_fallthrough()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Valid);

        Assert.Throws<ConfigException>(() => RelayConfig.Load(dir.File("nope.json"), true, dir.Path, NoEnv));
        Assert.Throws<ConfigException>(() => RelayConfig.Load(null, true, dir.Path, _ => dir.File("nope.json")));
    }

    [Fact]
    public void Subcommands_never_fall_back_to_the_example_or_a_parent_directory()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Valid);          // parent has a config
        var child = Directory.CreateDirectory(dir.File("child")).FullName;
        File.WriteAllText(Path.Combine(child, "config.example.json"), Valid);

        Assert.Throws<ConfigException>(() => RelayConfig.Load(null, false, child, NoEnv));
    }

    [Fact]
    public void Bridge_run_may_use_the_example_in_cwd_but_not_a_parent_config()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), "{\"channel\":\"parent\",\"nick\":\"n\"}");
        var child = Directory.CreateDirectory(dir.File("child")).FullName;
        File.WriteAllText(Path.Combine(child, "config.example.json"), "{\"channel\":\"example\",\"nick\":\"n\"}");

        var cfg = RelayConfig.Load(null, true, child, NoEnv);

        Assert.Equal("example", cfg.Channel);
        Assert.Equal("./config.example.json", cfg.Source);
    }

    [Fact]
    public void Relative_base_resolves_against_the_config_directory()
    {
        using var dir = new TempDir();
        var sub = Directory.CreateDirectory(dir.File("cfg")).FullName;
        File.WriteAllText(Path.Combine(sub, "c.json"), "{\"channel\":\"c\",\"nick\":\"n\",\"base\":\"run\"}");

        var cfg = RelayConfig.Load(Path.Combine(sub, "c.json"), false, dir.Path, NoEnv);

        Assert.Equal(Path.Combine(sub, "run"), cfg.BaseDir);
    }

    [Fact]
    public void Invalid_json_and_missing_fields_are_config_errors()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("bad.json"), "{nope");
        File.WriteAllText(dir.File("nochan.json"), "{\"nick\":\"n\"}");

        Assert.Throws<ConfigException>(() => RelayConfig.Load(dir.File("bad.json"), false, dir.Path, NoEnv));
        Assert.Throws<ConfigException>(() => RelayConfig.Load(dir.File("nochan.json"), false, dir.Path, NoEnv));
    }

    [Fact]
    public void Receive_idle_defaults_to_300_seconds()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("c.json"), Valid);

        var cfg = RelayConfig.Load(dir.File("c.json"), false, dir.Path, NoEnv);

        Assert.Equal(300, cfg.ReceiveIdleSeconds);
        Assert.Equal(RelayConfig.DefaultReceiveIdleSeconds, cfg.ReceiveIdleSeconds);
        Assert.Equal(TimeSpan.FromSeconds(300), cfg.ReceiveIdle);
        Assert.Equal(360, RelayConfig.RecommendedExternalWatchdogSeconds);
        Assert.True(RelayConfig.RecommendedExternalWatchdogSeconds > cfg.ReceiveIdleSeconds);
    }

    [Fact]
    public void Receive_idle_zero_disables_the_watchdog()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("c.json"), "{\"channel\":\"c\",\"nick\":\"n\",\"receive_idle_s\":0}");

        var cfg = RelayConfig.Load(dir.File("c.json"), false, dir.Path, NoEnv);

        Assert.Equal(0, cfg.ReceiveIdleSeconds);
        Assert.Equal(TimeSpan.Zero, cfg.ReceiveIdle);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("\"nope\"")]
    public void Receive_idle_out_of_range_is_a_config_error(string literal)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("c.json"), "{\"channel\":\"c\",\"nick\":\"n\",\"receive_idle_s\":" + literal + "}");

        var ex = Assert.Throws<ConfigException>(() => RelayConfig.Load(dir.File("c.json"), false, dir.Path, NoEnv));

        Assert.Contains("receive_idle_s", ex.Message, StringComparison.Ordinal);
    }
}

public class BackoffTests
{
    [Fact]
    public void Doubles_to_the_cap_while_sessions_fail()
    {
        var b = new Backoff();
        var delays = Enumerable.Range(0, 7).Select(_ => b.NextDelaySeconds(false, TimeSpan.FromSeconds(1))).ToArray();
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 30, 30 }, delays);
    }

    [Fact]
    public void Confirmed_session_resets_to_one_second()
    {
        var b = new Backoff();
        for (var i = 0; i < 6; i++) b.NextDelaySeconds(false, TimeSpan.Zero);
        Assert.Equal(1, b.NextDelaySeconds(true, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, b.NextDelaySeconds(true, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void A_minute_of_uptime_resets_even_without_onlineSet()
    {
        var b = new Backoff();
        for (var i = 0; i < 6; i++) b.NextDelaySeconds(false, TimeSpan.Zero);
        Assert.Equal(1, b.NextDelaySeconds(false, TimeSpan.FromSeconds(61)));
        Assert.Equal(2, b.NextDelaySeconds(false, TimeSpan.FromSeconds(1)));
    }
}
