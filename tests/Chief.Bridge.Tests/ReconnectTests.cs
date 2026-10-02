using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Chief.Bridge.Tests;

public class ReconnectTests
{
    [Fact]
    public void Jitter_stays_inside_twenty_percent_and_the_cap()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(800), Backoff.WithJitter(1, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), Backoff.WithJitter(1, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(1200), Backoff.WithJitter(1, 1));
        Assert.Equal(TimeSpan.FromSeconds(30), Backoff.WithJitter(30, 1));
        Assert.Equal(TimeSpan.FromSeconds(24), Backoff.WithJitter(30, 0));
        // A bad draw is treated as 0, which is the short end of the range (80%).
        Assert.Equal(TimeSpan.FromMilliseconds(800), Backoff.WithJitter(1, double.NaN));
    }

    [Fact]
    public async Task More_than_three_transient_failures_then_a_join_succeeds()
    {
        const string pass = "s3cret-trip-pass";
        await using var fx = new RelayFixture(pass);
        fx.Script.Enqueue(
            Attempt.ConnectThrows(new IOException($"dns lookup failed {pass}")),
            Attempt.ConnectThrows(new IOException("connection refused")),
            Attempt.ConnectThrows(new System.Security.Authentication.AuthenticationException("tls handshake failed")),
            Attempt.Warn("Nickname taken"),
            Attempt.CloseDuringJoin(),
            Attempt.Warn("You are joining channels too fast. Wait a moment and try again."),
            Attempt.OnlineSetHold(fx.Nick));

        await fx.RunUntilConnected();

        Assert.Equal(7, fx.Script.Created);
        Assert.Equal(
            new[] { 1, 2, 4, 8, 16, 30 }.Select(s => TimeSpan.FromSeconds(s)).ToArray(),
            fx.Delays);
        var inbox = File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl"));
        Assert.DoesNotContain(pass, inbox);
        Assert.Contains("join rejected: Nickname taken", inbox);
        Assert.Contains("\"attempt\":6", inbox);
        Assert.Contains(pass, fx.Script.SentText);
    }

    [Fact]
    public async Task A_drop_after_a_confirmed_join_reconnects()
    {
        await using var fx = new RelayFixture();
        fx.Script.Enqueue(Attempt.OnlineSetThenClose(fx.Nick), Attempt.OnlineSetHold(fx.Nick));

        await fx.RunUntilConnected(minCreated: 2);

        Assert.Equal(2, fx.Script.Created);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(fx.Delays));
        Assert.Contains("server closed the connection", File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl")));
    }

    [Fact]
    public async Task A_hung_connect_is_abandoned_and_retried()
    {
        await using var fx = new RelayFixture { ConnectTimeout = TimeSpan.FromMilliseconds(40) };
        fx.Script.Enqueue(
            Attempt.HangConnect(),
            Attempt.HangConnect(),
            Attempt.HangConnect(),
            Attempt.HangConnect(),
            Attempt.OnlineSetHold(fx.Nick));

        await fx.RunUntilConnected();

        Assert.Equal(5, fx.Script.Created);
        Assert.Contains("connect not completed within 40ms", File.ReadAllText(Path.Combine(fx.Dir.Path, "inbox.jsonl")));
    }

    [Fact]
    public async Task A_failure_to_write_the_log_or_state_does_not_exit()
    {
        await using var fx = new RelayFixture();
        Directory.CreateDirectory(Path.Combine(fx.Dir.Path, "inbox.jsonl"));
        Directory.CreateDirectory(Path.Combine(fx.Dir.Path, "state.json"));
        fx.Script.Enqueue(
            Attempt.ConnectThrows(new IOException("connection refused")),
            Attempt.ConnectThrows(new IOException("connection refused")),
            Attempt.ConnectThrows(new IOException("connection refused")),
            Attempt.ConnectThrows(new IOException("connection refused")));
        fx.CancelAfterCreates = 4;

        var ex = await Record.ExceptionAsync(() => fx.RunToCompletion());

        Assert.Null(ex);
        Assert.Equal(4, fx.Script.Created);
    }

    [Fact]
    public async Task A_closed_stdout_does_not_exit_the_retry_loop()
    {
        await using var fx = new RelayFixture
        {
            Stdout = _ => throw new IOException("stdout closed")
        };
        fx.Script.Enqueue(
            Attempt.ConnectThrows(new IOException("connection refused")),
            Attempt.ConnectThrows(new IOException("connection refused")),
            Attempt.ConnectThrows(new IOException("connection refused")),
            Attempt.ConnectThrows(new IOException("connection refused")));
        fx.CancelAfterCreates = 4;

        var ex = await Record.ExceptionAsync(() => fx.RunToCompletion());

        Assert.Null(ex);
        Assert.Equal(4, fx.Script.Created);
    }

    [Theory]
    [InlineData("ftp://example.com/chat")]
    [InlineData("http://hack.chat/chat-ws")]
    [InlineData("not a url")]
    public async Task A_url_that_can_never_work_fails_fast(string url)
    {
        await using var fx = new RelayFixture { Url = url };
        fx.Script.Enqueue(Attempt.OnlineSetHold("n"));

        var ex = await Assert.ThrowsAsync<ConfigException>(() => fx.RunToCompletion());

        Assert.Equal(0, fx.Script.Created);
        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public async Task A_real_socket_keeps_retrying_past_three_rejected_handshakes()
    {
        await using var server = await LocalChat.Start();
        await using var fx = new RelayFixture { Url = server.Url, UseRealSocket = true };
        server.RejectHandshakes(5);
        server.ThenOnlineSet("n");

        await fx.RunUntilConnected();

        Assert.True(server.Handshakes >= 6, $"server saw {server.Handshakes} handshakes");
    }

    [Fact]
    public void Muse_pages_build_is_the_vite_client()
    {
        var root = RepoRoot();
        var source = Path.Combine(root, "web", "muse");
        var published = Path.Combine(root, "docs", "muse");

        Assert.True(File.Exists(Path.Combine(source, "package.json")));
        Assert.True(File.Exists(Path.Combine(source, "src", "reconnect.js")));
        Assert.True(File.Exists(Path.Combine(source, "src", "App.vue")));

        var index = File.ReadAllText(Path.Combine(published, "index.html"));
        Assert.Contains("id=\"app\"", index, StringComparison.Ordinal);
        Assert.Contains("<script", index, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(published, "app.js")));
        Assert.False(File.Exists(Path.Combine(published, "reconnect.js")));

        var built = string.Concat(Directory.GetFiles(published, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        Assert.Contains("reconnecting in ", built, StringComparison.Ordinal);
        Assert.Contains("with a public trip", built, StringComparison.Ordinal);
        Assert.Contains("voizle-text-relay", built, StringComparison.Ordinal);
        Assert.DoesNotContain("hack.chat", built, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseChiefRelay.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}

internal sealed class RelayFixture : IAsyncDisposable
{
    public TempDir Dir { get; } = new();
    public string Nick { get; } = "n";
    public SocketScript Script { get; } = new();
    public List<TimeSpan> Delays { get; } = new();
    public string? Url { get; init; }
    public string? Trip { get; init; }
    public bool AutoAck { get; init; }
    public string[] MentionTrips { get; init; } = [];
    public bool UseRealSocket { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int CancelAfterCreates { get; set; }
    public Action<string>? Stdout { get; set; }
    public double? ReceiveIdleSeconds { get; init; }
    public Func<DateTimeOffset>? UtcNow { get; set; }
    public Func<TimeSpan, CancellationToken, Task>? IdleDelay { get; set; }
    public Action? OnBackoff { get; set; }
    public Func<string, string?>? Env { get; init; }
    public Action<string>? MirrorOffer { get; init; }

    private readonly string _pass;

    public RelayFixture(string pass = "") => _pass = pass;

    public async Task RunUntilConnected(int minCreated = 1)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var run = Run(cts);
        var statePath = Path.Combine(Dir.Path, "state.json");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        var connected = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            var attemptsReady = UseRealSocket || Script.Created >= minCreated;
            if (attemptsReady && StateFlag(statePath, "connected"))
            {
                connected = true;
                break;
            }

            await Task.Delay(15);
        }

        cts.Cancel();
        await run;
        Assert.True(connected, $"never connected (attempts={Script.Created})");
    }

    public async Task RunUntil(Func<bool> ready)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var run = Run(cts);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        var ok = false;
        while (DateTime.UtcNow < deadline)
        {
            if (ready())
            {
                ok = true;
                break;
            }

            await Task.Delay(15);
        }

        cts.Cancel();
        await run;
        Assert.True(ok, $"condition not met (attempts={Script.Created}, delays={Delays.Count})");
    }

    public async Task RunToCompletion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Run(cts);
    }

    private async Task Run(CancellationTokenSource cts)
    {
        var cfg = WriteConfig();
        var utcNow = UtcNow;
        var idleDelay = IdleDelay;
        var runtime = new BridgeRuntime
        {
            SocketFactory = timeout =>
            {
                if (UseRealSocket)
                    return new ClientRelaySocket(cfg.Origin, timeout);
                var socket = Script.Create(timeout);
                if (CancelAfterCreates > 0 && Script.Created >= CancelAfterCreates)
                    cts.Cancel();
                return socket;
            },
            Delay = (delay, token) =>
            {
                OnBackoff?.Invoke();
                Delays.Add(delay);
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            JitterUnit = () => 0.5,
            ConnectTimeout = ConnectTimeout,
            JoinTimeout = JoinTimeout,
            CloseGrace = TimeSpan.FromMilliseconds(30),
            OutboxPoll = TimeSpan.FromMilliseconds(20),
            Stdout = Stdout,
            UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow),
            IdleDelay = idleDelay ?? ((delay, token) => Task.Delay(delay, token)),
            // Tests do not follow the process environment. An operator flag must not
            // start the Node helper during a socket script.
            Env = key => Env?.Invoke(key),
            MirrorOffer = MirrorOffer
        };

        var bridge = new HackChatBridge(cfg, runtime);
        await bridge.RunForeverAsync(cts.Token);
    }

    public ValueTask DisposeAsync()
    {
        Dir.Dispose();
        return ValueTask.CompletedTask;
    }

    private RelayConfig WriteConfig()
    {
        var path = Dir.File("config.json");
        var url = Url ?? "wss://hack.chat/chat-ws";
        var doc = new Dictionary<string, object?>
        {
            ["url"] = url,
            ["origin"] = "https://hack.chat",
            ["channel"] = "throwaway-test",
            ["nick"] = Nick,
            ["pass"] = _pass,
            ["base"] = "."
        };
        if (!string.IsNullOrEmpty(Trip))
            doc["trip"] = Trip;
        if (AutoAck)
        {
            doc["auto_ack"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["mention_trips"] = MentionTrips,
                ["cooldown_s"] = 10
            };
        }
        if (ReceiveIdleSeconds is { } idle)
            doc["receive_idle_s"] = idle;
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
        return RelayConfig.Load(path, false, Dir.Path, _ => null);
    }

    private static bool StateFlag(string path, string name)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }
}

internal sealed class SocketScript
{
    private readonly Queue<Attempt> _attempts = new();
    public int Created { get; private set; }
    public List<string> Sent { get; } = new();
    public string SentText => string.Join("\n", Sent);
    public ScriptedSocket? Latest { get; private set; }

    public void Enqueue(params Attempt[] attempts)
    {
        foreach (var a in attempts)
            _attempts.Enqueue(a);
    }

    public IRelaySocket Create(TimeSpan _)
    {
        Created++;
        var attempt = _attempts.Count > 0 ? _attempts.Dequeue() : Attempt.CloseDuringJoin();
        var socket = new ScriptedSocket(attempt, Sent);
        Latest = socket;
        return socket;
    }
}

internal sealed class Attempt
{
    public Exception? ConnectError { get; init; }
    public bool Hang { get; init; }
    public bool Hold { get; init; }
    public string[] Messages { get; init; } = [];

    public static Attempt ConnectThrows(Exception ex) => new() { ConnectError = ex };
    public static Attempt HangConnect() => new() { Hang = true };
    public static Attempt Warn(string text) => new()
    {
        Messages = [JsonSerializer.Serialize(new { cmd = "warn", text })]
    };
    public static Attempt CloseDuringJoin() => new();
    public static Attempt HoldBeforeJoin() => new() { Hold = true };
    public static Attempt OnlineSetHold(string nick) => OnlineSet(nick, hold: true);
    public static Attempt OnlineSetThenClose(string nick) => OnlineSet(nick, hold: false);

    private static Attempt OnlineSet(string nick, bool hold) => new()
    {
        Hold = hold,
        Messages =
        [
            JsonSerializer.Serialize(new
            {
                cmd = "onlineSet",
                nicks = new[] { nick },
                users = new[] { new { nick, isme = true, trip = "AbCdEf" } }
            })
        ]
    };
}

internal sealed class ScriptedSocket(Attempt attempt, List<string> sent) : IRelaySocket
{
    private readonly ConcurrentQueue<byte[]> _incoming = new(attempt.Messages.Select(m => Encoding.UTF8.GetBytes(m)));
    private readonly SemaphoreSlim _ready = new(0);
    private bool _open;
    private int _forceClose;

    public bool CanCloseOutput => _open;

    public void Push(string json)
    {
        _incoming.Enqueue(Encoding.UTF8.GetBytes(json));
        _ready.Release();
    }

    /// <summary>End a held socket so the bridge reconnects. Queued frames are still delivered first.</summary>
    public void Close()
    {
        Interlocked.Exchange(ref _forceClose, 1);
        _ready.Release();
    }

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (attempt.Hang)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (attempt.ConnectError is { } error)
            throw error;
        _open = true;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        sent.Add(Encoding.UTF8.GetString(buffer.Span));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<RelayReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_incoming.TryDequeue(out var msg))
            {
                if (msg.Length > buffer.Length)
                    throw new InvalidOperationException("test frame does not fit the receive buffer");
                msg.CopyTo(buffer.Span);
                return new RelayReceiveResult(msg.Length, true, false, null, null);
            }

            if (Volatile.Read(ref _forceClose) != 0 || !attempt.Hold)
            {
                _open = false;
                return RelayReceiveResult.Closed("Close", null);
            }

            await _ready.WaitAsync(cancellationToken);
        }
    }

    public ValueTask CloseOutputAsync(CancellationToken cancellationToken)
    {
        _open = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A localhost hack.chat stand-in. It never dials the public service.</summary>
internal sealed class LocalChat : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _rejectsLeft;
    private int _handshakes;
    private volatile bool _accept;

    public string Url { get; }
    public int Handshakes => _handshakes;

    private LocalChat(HttpListener listener, int port)
    {
        _listener = listener;
        Url = $"ws://127.0.0.1:{port}/";
        _loop = Task.Run(Serve);
    }

    public static Task<LocalChat> Start()
    {
        var port = 18080 + Random.Shared.Next(0, 2000);
        for (var i = 0; i < 20; i++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return Task.FromResult(new LocalChat(listener, port));
            }
            catch (HttpListenerException)
            {
                listener.Close();
                port++;
            }
        }

        throw new InvalidOperationException("no free localhost port");
    }

    public void RejectHandshakes(int count) => _rejectsLeft = count;

    public void ThenOnlineSet(string nick)
    {
        _nick = nick;
        _accept = true;
    }

    private string _nick = "n";

    private async Task Serve()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            Interlocked.Increment(ref _handshakes);
            if (Interlocked.Decrement(ref _rejectsLeft) >= 0)
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                catch { /* client already gone */ }
                continue;
            }

            if (!_accept || !ctx.Request.IsWebSocketRequest)
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
                continue;
            }

            WebSocket? ws = null;
            try
            {
                var accepted = await ctx.AcceptWebSocketAsync(subProtocol: null);
                ws = accepted.WebSocket;
                // The bridge treats every non-hack.chat host, including this localhost stand-in, as v1.
                var hello = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { v = 1, type = "hello", protocol = "voizle-text-relay" }));
                await ws.SendAsync(hello, WebSocketMessageType.Text, true, CancellationToken.None);
                var buf = new byte[1024];
                var welcomed = false;
                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var incoming = await ws.ReceiveAsync(buf, _cts.Token);
                    if (incoming.MessageType == WebSocketMessageType.Close)
                        break;
                    if (welcomed || !incoming.EndOfMessage)
                        continue;
                    welcomed = true;
                    var welcome = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        v = 1,
                        type = "welcome",
                        sessionId = "local",
                        room = "throwaway-test",
                        nick = _nick,
                        users = new[] { new { sessionId = "local", nick = _nick } }
                    }));
                    await ws.SendAsync(welcome, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch
            {
                // the bridge closed, or the test ended
            }
            finally
            {
                try { ws?.Abort(); } catch { /* ignore */ }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
