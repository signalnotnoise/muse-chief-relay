using System.Text.Json;

namespace Chief.Bridge.Tests;

public class ReceiveIdleTests
{
    [Fact]
    public async Task Quiet_socket_after_join_ends_the_session_and_reconnects()
    {
        const string pass = "s3cret-trip-pass";
        var clock = new ManualClock();
        var waits = new List<TimeSpan>();
        string? reconnecting = null;
        await using var fx = new RelayFixture(pass)
        {
            UtcNow = clock.UtcNow,
            IdleDelay = (wait, _) =>
            {
                waits.Add(wait);
                clock.Advance(wait);
                return Task.CompletedTask;
            }
        };
        fx.Script.Enqueue(Attempt.OnlineSetHold(fx.Nick));
        fx.CancelAfterCreates = 2;
        var statePath = Path.Combine(fx.Dir.Path, "state.json");
        fx.OnBackoff = () =>
        {
            reconnecting ??= File.Exists(statePath) ? File.ReadAllText(statePath) : "";
        };

        await fx.RunUntil(() => reconnecting is not null);

        Assert.Equal(TimeSpan.FromSeconds(RelayConfig.DefaultReceiveIdleSeconds), Assert.Single(waits));
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(fx.Delays));
        Assert.False(string.IsNullOrEmpty(reconnecting));
        using (var doc = JsonDocument.Parse(reconnecting))
        {
            Assert.False(doc.RootElement.GetProperty("connected").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("reconnecting").GetBoolean());
            var reason = doc.RootElement.GetProperty("reason").GetString();
            Assert.StartsWith("receive_idle", reason, StringComparison.Ordinal);
            Assert.Contains("quiet socket", reason, StringComparison.Ordinal);
            Assert.DoesNotContain(pass, reason, StringComparison.Ordinal);
        }

        var inbox = File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl"));
        Assert.Contains("receive_idle", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain(pass, inbox, StringComparison.Ordinal);
        Assert.DoesNotContain(pass, reconnecting, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_inbound_frame_resets_the_idle_clock()
    {
        var clock = new ManualClock();
        var waits = new List<TimeSpan>();
        var idleReconnects = 0;
        await using var fx = new RelayFixture { UtcNow = clock.UtcNow };
        var inboxPath = Path.Combine(fx.Dir.Path, "inbox.jsonl");
        fx.IdleDelay = async (wait, token) =>
        {
            waits.Add(wait);
            if (waits.Count == 1)
            {
                // Not chat, and not presence. Any server frame has to push the deadline out.
                clock.Advance(TimeSpan.FromSeconds(250));
                var socket = fx.Script.Latest ?? throw new InvalidOperationException("no socket");
                socket.Push("""{"cmd":"info","text":"still-here"}""");
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.Exists(inboxPath)
                            && File.ReadAllText(inboxPath).Contains("\"cmd\":\"info\"", StringComparison.Ordinal))
                            return;
                    }
                    catch (IOException)
                    {
                        // the bridge is appending the line
                    }

                    await Task.Delay(10, token);
                }

                throw new TimeoutException("info frame was not logged");
            }

            clock.Advance(wait);
        };
        fx.Script.Enqueue(Attempt.OnlineSetHold(fx.Nick));
        var statePath = Path.Combine(fx.Dir.Path, "state.json");
        fx.OnBackoff = () =>
        {
            if (File.Exists(statePath) && File.ReadAllText(statePath).Contains("receive_idle", StringComparison.Ordinal))
                Interlocked.Increment(ref idleReconnects);
        };

        await fx.RunUntil(() => Volatile.Read(ref idleReconnects) >= 1);

        Assert.Equal(2, waits.Count);
        Assert.Equal(TimeSpan.FromSeconds(300), waits[0]);
        // The info frame was stamped after 250s of the first window, so the next wait is a full 300s
        // rather than the 50s that would have remained.
        Assert.Equal(TimeSpan.FromSeconds(300), waits[1]);
        Assert.Equal(1, Volatile.Read(ref idleReconnects));
        Assert.Contains("\"cmd\":\"info\"", File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Idle_timer_stays_disarmed_before_join_and_during_backoff()
    {
        var clock = new ManualClock();
        var idleCalls = 0;
        var samples = new List<(int Before, int After, string State)>();
        await using var fx = new RelayFixture
        {
            JoinTimeout = TimeSpan.FromMilliseconds(400),
            UtcNow = clock.UtcNow,
            IdleDelay = (wait, _) =>
            {
                Interlocked.Increment(ref idleCalls);
                clock.Advance(wait);
                return Task.CompletedTask;
            }
        };
        fx.CancelAfterCreates = 3;
        fx.Script.Enqueue(Attempt.OnlineSetHold(fx.Nick), Attempt.HoldBeforeJoin());
        var statePath = Path.Combine(fx.Dir.Path, "state.json");
        fx.OnBackoff = () =>
        {
            var before = Volatile.Read(ref idleCalls);
            var state = File.Exists(statePath) ? File.ReadAllText(statePath) : "";
            Thread.Sleep(50);
            samples.Add((before, Volatile.Read(ref idleCalls), state));
        };

        await fx.RunToCompletion();

        // One fire after the confirmed join. None during either backoff, none before the
        // second join (it never confirms), and none on the third attempt (cancelled at connect).
        Assert.Equal(1, Volatile.Read(ref idleCalls));
        Assert.Equal(2, samples.Count);
        Assert.All(samples, sample => Assert.Equal(sample.Before, sample.After));

        using (var first = JsonDocument.Parse(samples[0].State))
        {
            Assert.True(first.RootElement.GetProperty("reconnecting").GetBoolean());
            Assert.StartsWith("receive_idle", first.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        }

        using (var second = JsonDocument.Parse(samples[1].State))
        {
            Assert.True(second.RootElement.GetProperty("reconnecting").GetBoolean());
            Assert.DoesNotContain("receive_idle", second.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Zero_receive_idle_does_not_end_a_quiet_session()
    {
        var calls = 0;
        await using var fx = new RelayFixture { ReceiveIdleSeconds = 0 };
        fx.Script.Enqueue(Attempt.OnlineSetHold(fx.Nick));
        fx.IdleDelay = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        };

        await fx.RunUntilConnected();

        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(1, fx.Script.Created);
        Assert.Empty(fx.Delays);
    }
}

internal sealed class ManualClock
{
    private long _ticks = DateTimeOffset.UnixEpoch.UtcTicks;

    public DateTimeOffset UtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
