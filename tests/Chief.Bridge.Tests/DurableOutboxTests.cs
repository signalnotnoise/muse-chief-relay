using System.Text.Json;

namespace Chief.Bridge.Tests;

public class DurableOutboxTests
{
    private static string Request(string baseDir, string text = "@Fixture hello", char key = 'a')
    {
        var root = Path.Combine(baseDir, "durable-outbox");
        Directory.CreateDirectory(root);
        var id = new string(key, 64);
        File.WriteAllText(Path.Combine(root, id + ".json"), JsonSerializer.Serialize(new
        {
            id, eventId = "event-" + key, to = "Fixture", text
        }));
        return id;
    }

    [Fact]
    public void Pending_reply_survives_restart_and_an_inflight_reply_is_not_resent()
    {
        using var dir = new TempDir();
        var id = Request(dir.Path);
        var queue = new DurableOutbox(dir.Path);
        Assert.Single(queue.Pending());
        Assert.Single(new DurableOutbox(dir.Path).Pending());
        queue.BeginSend(id);
        var recovered = new DurableOutbox(dir.Path);
        Assert.Equal("uncertain", recovered.State(id));
        Assert.Empty(recovered.Pending());
        recovered.Resolve(id, "requeue");
        Assert.Single(recovered.Pending());
    }

    [Fact]
    public void Echo_can_arrive_before_send_returns_without_being_overwritten()
    {
        using var dir = new TempDir();
        var id = Request(dir.Path);
        var queue = new DurableOutbox(dir.Path);
        queue.BeginSend(id);
        queue.ObserveEcho("someone-else", "@Fixture hello", "dot");
        Assert.Equal("sending", queue.State(id));
        queue.ObserveEcho("dot", "@Fixture hello", "dot");
        queue.Sent(id);
        queue.Uncertain(id);
        Assert.Equal("echo_observed", queue.State(id));
        Assert.Empty(new DurableOutbox(dir.Path).Pending());
    }

    [Fact]
    public void A_sent_reply_is_preserved_unconfirmed_until_explicit_resolution()
    {
        using var dir = new TempDir();
        var id = Request(dir.Path);
        var queue = new DurableOutbox(dir.Path);
        queue.BeginSend(id);
        queue.Sent(id);
        var recovered = new DurableOutbox(dir.Path);
        Assert.Equal("sent", recovered.State(id));
        Assert.Empty(recovered.Pending());
        recovered.Resolve(id, "drop");
        Assert.Equal("dropped", new DurableOutbox(dir.Path).State(id));
    }

    [Fact]
    public void Invalid_or_symlinked_request_fails_closed()
    {
        using var dir = new TempDir();
        var id = Request(dir.Path);
        var file = Path.Combine(dir.Path, "durable-outbox", id + ".json");
        File.WriteAllText(file, "{\"id\":\"wrong\"}");
        Assert.Throws<ConfigException>(() => new DurableOutbox(dir.Path));
        File.Delete(file);
        File.CreateSymbolicLink(file, dir.File("other"));
        Assert.Throws<ConfigException>(() => new DurableOutbox(dir.Path));
    }

    [Fact]
    public async Task Bridge_drains_requests_created_before_start_only_after_join()
    {
        await using var fx = new RelayFixture { DurableOutbox = true };
        var id = Request(fx.Dir.Path);
        fx.Script.Enqueue(Attempt.OnlineSetHold(fx.Nick));
        await fx.RunUntil(() => fx.Script.SentText.Contains("@Fixture hello"));
        var frames = fx.Script.Sent.Select(s => JsonDocument.Parse(s)).ToList();
        try
        {
            Assert.Equal("join", frames[0].RootElement.GetProperty("cmd").GetString());
            Assert.Single(frames.Where(d => d.RootElement.TryGetProperty("text", out var t) && t.GetString() == "@Fixture hello"));
            Assert.Equal("sent", new DurableOutbox(fx.Dir.Path).State(id));
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }
}
