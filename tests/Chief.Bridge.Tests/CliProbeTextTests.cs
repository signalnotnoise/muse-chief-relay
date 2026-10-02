using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

public class CliProbeTextTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--")]
    [InlineData("help")]
    [InlineData("  --help  ")]
    [InlineData("\"$MSG\"")]
    [InlineData("'$REPLY'")]
    [InlineData("  '$REPLY'  ")]
    [InlineData("$REPLY")]
    [InlineData("$1")]
    [InlineData("$MSG")]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("--foo-bar")]
    [InlineData("--foo_bar")]
    [InlineData("\"--help\"")]
    [InlineData("'help'")]
    [InlineData("\"  --help  \"")]
    public void Probe_leftovers_are_fragments(string text) =>
        Assert.True(CliProbeText.IsFragment(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("$")]
    [InlineData("${REPLY}")]
    [InlineData("Help")]
    [InlineData("use --help carefully")]
    [InlineData("price is $5 today")]
    [InlineData("@Alex status")]
    [InlineData("hello")]
    [InlineData("\"hello there\"")]
    [InlineData("\"use --help carefully\"")]
    public void Normal_chat_is_not_a_fragment(string? text) =>
        Assert.False(CliProbeText.IsFragment(text));

    [Fact]
    public void BuildAll_drops_help_chat_and_keeps_a_real_chat()
    {
        Assert.Empty(OutboxPayload.BuildAll("{\"cmd\":\"chat\",\"text\":\"--help\"}"));
        Assert.Empty(OutboxPayload.BuildAll("{\"text\":\"--help\"}"));
        Assert.Empty(OutboxPayload.BuildAll("{\"cmd\":\"chat\",\"text\":\"\\\"$MSG\\\"\"}"));
        Assert.Empty(OutboxPayload.BuildAll("{\"cmd\":\"chat\",\"text\":\"'$REPLY'\"}"));
        Assert.Empty(OutboxPayload.BuildAll("{\"text\":\"$REPLY\"}"));
        // A probe beside a real chat fail-closes the whole line.
        Assert.Empty(OutboxPayload.BuildAll(
            "{\"cmd\":\"chat\",\"text\":\"hello\"}{\"cmd\":\"chat\",\"text\":\"--help\"}"));

        var kept = Assert.Single(OutboxPayload.BuildAll("{\"cmd\":\"chat\",\"text\":\"hello\"}"));
        Assert.Equal("chat", kept["cmd"]!.GetValue<string>());
        Assert.Equal("hello", kept["text"]!.GetValue<string>());

        var sentence = Assert.Single(OutboxPayload.BuildAll(
            "{\"cmd\":\"chat\",\"text\":\"use --help carefully\"}"));
        Assert.Equal("use --help carefully", sentence["text"]!.GetValue<string>());

        var price = Assert.Single(OutboxPayload.BuildAll(
            "{\"text\":\"price is $5 today\"}"));
        Assert.Equal("price is $5 today", price["text"]!.GetValue<string>());

        // Non-chat commands are unchanged even when the text looks like a probe.
        var emote = Assert.Single(OutboxPayload.BuildAll("{\"cmd\":\"emote\",\"text\":\"--help\"}"));
        Assert.Equal("emote", emote["cmd"]!.GetValue<string>());
        Assert.Equal("--help", emote["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("$REPLY")]
    [InlineData("$MSG")]
    public async Task Say_refuses_probe_text_without_queueing(string text)
    {
        using var dir = new TempDir();
        var cfg = WriteConfig(dir);
        var (code, _, err) = await RunAsync("say", "--config", cfg, text);

        Assert.Equal(1, code);
        Assert.Contains("ChatBridge say: refused CLI/shell probe text (not queued)", err);
        Assert.False(File.Exists(Path.Combine(dir.Path, "outbox.jsonl")));
    }

    [Fact]
    public async Task Say_refuses_a_quoted_shell_leftover_passed_after_end_of_options()
    {
        using var dir = new TempDir();
        var cfg = WriteConfig(dir);
        var (code, _, err) = await RunAsync("say", "--config", cfg, "--", "\"$MSG\"");

        Assert.Equal(1, code);
        Assert.Contains("ChatBridge say: refused CLI/shell probe text (not queued)", err);
        Assert.False(File.Exists(Path.Combine(dir.Path, "outbox.jsonl")));
    }

    [Fact]
    public async Task Say_still_queues_a_normal_sentence_that_mentions_a_flag()
    {
        using var dir = new TempDir();
        var cfg = WriteConfig(dir);
        var (code, stdout, err) = await RunAsync("say", "--config", cfg, "use", "--help", "carefully");

        Assert.Equal(0, code);
        Assert.DoesNotContain("refused CLI/shell probe text", err);
        var line = Assert.Single(File.ReadAllLines(Path.Combine(dir.Path, "outbox.jsonl")));
        var obj = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("chat", obj["cmd"]!.GetValue<string>());
        Assert.Equal("use --help carefully", obj["text"]!.GetValue<string>());
        Assert.Contains("queued ->", stdout);
    }

    private static string WriteConfig(TempDir dir)
    {
        var path = dir.File("config.json");
        File.WriteAllText(path, "{\"channel\":\"c\",\"nick\":\"n\"}");
        return path;
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
}
