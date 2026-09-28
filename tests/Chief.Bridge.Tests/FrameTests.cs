using System.Text;
using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

public class MessageAssemblerTests
{
    [Fact]
    public void Multibyte_character_split_across_fragments_is_intact()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"café ✓ 日本\"}");
        var split = Array.IndexOf(bytes, (byte)0xE2) + 1; // inside the 3-byte ✓
        var asm = new MessageAssembler(1024);

        Assert.Null(asm.Append(bytes.AsSpan(0, split), endOfMessage: false, out _));
        var text = asm.Append(bytes.AsSpan(split), endOfMessage: true, out var dropped);

        Assert.False(dropped);
        Assert.Equal("{\"text\":\"café ✓ 日本\"}", text);
    }

    [Fact]
    public void Oversized_message_is_dropped_and_the_next_one_is_fine()
    {
        var asm = new MessageAssembler(8);
        Assert.Null(asm.Append(new byte[6], false, out _));
        Assert.Null(asm.Append(new byte[6], true, out var dropped));
        Assert.True(dropped);

        Assert.Equal("ok", asm.Append("ok"u8, true, out dropped));
        Assert.False(dropped);
    }
}

public class InboundFrameTests
{
    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("not json {")]
    public void Non_object_frames_are_logged_raw_and_not_handled(string raw)
    {
        var f = InboundFrame.Parse(raw);

        Assert.Null(f.Object);
        Assert.Null(f.Cmd);
        Assert.Equal(raw, f.LogNode["raw"]!.GetValue<string>());
    }

    [Fact]
    public void Non_string_cmd_is_ignored()
    {
        var f = InboundFrame.Parse("{\"cmd\":7}");
        Assert.NotNull(f.Object);
        Assert.Null(f.Cmd);
    }

    [Fact]
    public void Session_token_is_redacted_in_the_log_copy_only()
    {
        var f = InboundFrame.Parse("{\"cmd\":\"session\",\"restored\":false,\"token\":\"eyJsecret\"}");

        Assert.Equal("session", f.Cmd);
        Assert.Equal(LogRedaction.Redacted, f.LogNode["token"]!.GetValue<string>());
        Assert.DoesNotContain("eyJsecret", f.LogNode.ToJsonString());
        Assert.Equal("eyJsecret", f.Object!["token"]!.GetValue<string>());
    }

    [Fact]
    public void Chat_frames_keep_their_token_like_text()
    {
        var f = InboundFrame.Parse("{\"cmd\":\"chat\",\"text\":\"token\",\"token\":\"x\"}");
        Assert.Equal("x", f.LogNode["token"]!.GetValue<string>());
    }

    [Fact]
    public void Outbound_log_copy_redacts_pass()
    {
        var node = JsonNode.Parse("{\"cmd\":\"join\",\"channel\":\"c\",\"nick\":\"n\",\"pass\":\"hunter2\"}")!;
        var logged = LogRedaction.Outbound(node).ToJsonString();

        Assert.DoesNotContain("hunter2", logged);
        Assert.Equal("hunter2", node["pass"]!.GetValue<string>());
    }
}

public class OutboxPayloadTests
{
    [Fact]
    public void Blank_line_sends_nothing() => Assert.Empty(OutboxPayload.BuildAll("   "));

    [Fact]
    public void Object_with_cmd_passes_through()
    {
        var p = Assert.Single(OutboxPayload.BuildAll("{\"cmd\":\"emote\",\"text\":\"waves\"}"));
        Assert.Equal("emote", p["cmd"]!.GetValue<string>());
    }

    [Fact]
    public void Object_with_text_only_becomes_chat()
    {
        var p = Assert.Single(OutboxPayload.BuildAll("{\"text\":\"hi\"}"));
        Assert.Equal("{\"cmd\":\"chat\",\"text\":\"hi\"}", p.ToJsonString(JsonUtil.Opts));
    }

    [Fact]
    public void Concatenated_envelopes_become_separate_chats()
    {
        // 2026-09-27: two appends lost the newline between them and one line held two
        // envelopes; the old verbatim fallthrough put raw JSON in the channel.
        var ps = OutboxPayload.BuildAll(
            "{\"cmd\":\"chat\",\"text\":\"first\"}{\"cmd\":\"chat\",\"text\":\"second\"}");
        Assert.Equal(2, ps.Count);
        foreach (var p in ps)
            Assert.Equal("chat", p["cmd"]!.GetValue<string>());
        Assert.Equal("first", ps[0]["text"]!.GetValue<string>());
        Assert.Equal("second", ps[1]["text"]!.GetValue<string>());
    }

    [Fact]
    public void Concatenated_envelopes_may_mix_cmd_and_text_forms()
    {
        var ps = OutboxPayload.BuildAll("{\"cmd\":\"emote\",\"text\":\"waves\"} {\"text\":\"hi\"}");
        Assert.Equal(2, ps.Count);
        Assert.Equal("emote", ps[0]["cmd"]!.GetValue<string>());
        Assert.Equal("{\"cmd\":\"chat\",\"text\":\"hi\"}", ps[1].ToJsonString(JsonUtil.Opts));
    }

    [Fact]
    public void Malformed_line_sends_nothing_fail_closed()
    {
        // Plain text, JSON that is not a sendable envelope, trailing garbage, and
        // truncated input are all dropped — never sent verbatim.
        Assert.Empty(OutboxPayload.BuildAll("hello there"));
        Assert.Empty(OutboxPayload.BuildAll("[1,2]"));
        Assert.Empty(OutboxPayload.BuildAll("{\"type\":\"result\",\"body\":\"x\"}"));
        Assert.Empty(OutboxPayload.BuildAll("{\"cmd\":\"chat\",\"text\":\"a\"}trailing garbage"));
        Assert.Empty(OutboxPayload.BuildAll("{\"cmd\":\"chat\",\"text\":\"unterminated"));
        Assert.Empty(OutboxPayload.BuildAll(
            "{\"cmd\":\"chat\",\"text\":\"a\"}{\"cmd\":\"chat\",\"text\":\"b\"}trailing"));
    }

    [Fact]
    public void Mixed_valid_and_nonsendable_value_drops_the_whole_line()
    {
        // A sendable envelope glued to a protocol object, an array, or another
        // non-envelope yields zero frames. Sending the valid neighbor would
        // break the fail-closed contract.
        Assert.Empty(OutboxPayload.BuildAll(
            "{\"cmd\":\"chat\",\"text\":\"a\"}{\"type\":\"result\",\"body\":\"x\"}"));
        Assert.Empty(OutboxPayload.BuildAll(
            "{\"type\":\"result\",\"body\":\"x\"}{\"cmd\":\"chat\",\"text\":\"a\"}"));
        Assert.Empty(OutboxPayload.BuildAll(
            "{\"cmd\":\"chat\",\"text\":\"a\"}[1,2]"));
        Assert.Empty(OutboxPayload.BuildAll(
            "{\"text\":\"hi\"} {\"nope\":true}"));
    }

    [Fact]
    public void Dropped_line_diagnostic_is_length_only()
    {
        const string secret = "hunter2-session-token";
        var line = "  {\"type\":\"result\",\"pass\":\"" + secret + "\",\"token\":\"eyJabc\"}  ";
        var diag = LogRedaction.DroppedOutboxLine(line);

        Assert.Equal($"outbox: dropped malformed line ({line.Trim().Length} chars)", diag);
        Assert.DoesNotContain(secret, diag);
        Assert.DoesNotContain("eyJabc", diag);
        Assert.DoesNotContain("pass", diag);
        Assert.DoesNotContain("token", diag);
        Assert.DoesNotContain("result", diag);
        Assert.DoesNotContain("{", diag);
    }
}

public class JsonUtilTests
{
    [Fact]
    public void Apostrophe_and_non_ascii_are_not_escaped()
    {
        var json = new JsonObject { ["text"] = "chief's café ✓ 日本" }.ToJsonString(JsonUtil.Opts);
        Assert.Equal("{\"text\":\"chief's café ✓ 日本\"}", json);
    }
}
