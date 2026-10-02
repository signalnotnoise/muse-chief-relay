using System.Net.WebSockets;

namespace ChatBridge;

internal readonly record struct RelayReceiveResult(
    int Count,
    bool EndOfMessage,
    bool IsClose,
    string? CloseStatus,
    string? CloseDescription)
{
    public static RelayReceiveResult Closed(string? status, string? description) =>
        new(0, true, true, status, description);
}

/// <summary>One WebSocket attempt. The bridge creates a new one for every reconnect.</summary>
internal interface IRelaySocket : IAsyncDisposable
{
    /// <summary>True when a close frame can still be sent.</summary>
    bool CanCloseOutput { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    ValueTask<RelayReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask CloseOutputAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Knobs the reconnect loop needs. Production uses <see cref="For"/>; tests supply a fake socket,
/// a short timeout, and a delay that doesn't sleep.
/// </summary>
internal sealed class BridgeRuntime
{
    public Func<TimeSpan, IRelaySocket>? SocketFactory { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan CloseGrace { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan OutboxPoll { get; init; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Clock for the receive-idle watchdog. Tests can move it.</summary>
    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Wait used only by the receive-idle watchdog. Production sleeps. A test that skips reconnect
    /// backoff via <see cref="Delay"/> can still advance <see cref="UtcNow"/> here. The watchdog is
    /// not running during backoff, so this wait must not be folded into <see cref="Delay"/>.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> IdleDelay { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Uniform [0, 1] draw for reconnect jitter. Failures here are treated as 0.5.</summary>
    public Func<double> JitterUnit { get; init; } = static () => Random.Shared.NextDouble();

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Where operator lines go. Null writes to stdout. A throw here is ignored.</summary>
    public Action<string>? Stdout { get; init; }

    /// <summary>
    /// Environment lookup for the HIVEMIND mirror gate. Null uses
    /// <see cref="Environment.GetEnvironmentVariable"/>. The mirror stays off unless
    /// <c>HIVEMIND_MESSAGE_MIRROR</c> is <c>1</c>.
    /// </summary>
    public Func<string, string?>? Env { get; init; }

    /// <summary>
    /// Test stand-in for the Node helper. When the mirror flag is on, each accepted
    /// chat is handed here as one JSON object (no channel name, trip, or secret).
    /// Null starts the Node helper instead.
    /// </summary>
    public Action<string>? MirrorOffer { get; init; }

    public static BridgeRuntime For(RelayConfig cfg) => new()
    {
        SocketFactory = timeout => new ClientRelaySocket(cfg.Origin, timeout)
    };
}

/// <summary>Why a URL will never connect, no matter how many times we try.</summary>
internal static class RelayUrl
{
    /// <summary>
    /// Null when <paramref name="url"/> is an absolute <c>ws://</c> or <c>wss://</c> URI with a host.
    /// Anything else is a bad config: retrying it cannot succeed.
    /// </summary>
    public static string? PermanentProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "url is not an absolute ws:// or wss:// URI";
        if (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss)
            return $"url scheme '{uri.Scheme}' is not ws or wss";
        if (string.IsNullOrEmpty(uri.IdnHost))
            return "url has no host";
        return null;
    }

    /// <summary>False for hack.chat hosts, which still use the cmd/channel join. Every other host speaks v1.</summary>
    public static bool SpeaksVoizle(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.IdnHost))
            return false;
        var host = uri.IdnHost;
        if (host.Equals("hack.chat", StringComparison.OrdinalIgnoreCase))
            return false;
        return !host.EndsWith(".hack.chat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The URL as logged. Userinfo (a password in the URL) is removed.</summary>
    public static string ForLog(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return url;
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        return builder.Uri.AbsoluteUri;
    }
}

/// <summary>
/// A real hack.chat socket. Each attempt builds its own <see cref="SocketsHttpHandler"/> and disposes
/// it with the socket. <see cref="ClientWebSocket"/> would otherwise reuse one static handler for the
/// life of the process; that handler gives a single handshake only three internal connection retries
/// and can stall later attempts after a bad one.
/// </summary>
internal sealed class ClientRelaySocket : IRelaySocket
{
    private readonly ClientWebSocket _ws = new();
    private readonly string _origin;
    private readonly TimeSpan _connectTimeout;
    private HttpMessageInvoker? _invoker;

    public ClientRelaySocket(string origin, TimeSpan connectTimeout)
    {
        _origin = origin;
        _connectTimeout = connectTimeout;
    }

    public bool CanCloseOutput =>
        _ws.State is WebSocketState.Open or WebSocketState.CloseReceived;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        _ws.Options.SetRequestHeader("Origin", _origin);
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = _connectTimeout,
            // A proxy in the environment must not intercept a local test or a loopback URL.
            UseProxy = !uri.IsLoopback
        };
        _invoker = new HttpMessageInvoker(handler);
        await _ws.ConnectAsync(uri, _invoker, cancellationToken);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        _ws.SendAsync(buffer, WebSocketMessageType.Text, true, cancellationToken);

    public async ValueTask<RelayReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await _ws.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
            return RelayReceiveResult.Closed(_ws.CloseStatus?.ToString(), _ws.CloseStatusDescription);
        return new RelayReceiveResult(result.Count, result.EndOfMessage, false, null, null);
    }

    public ValueTask CloseOutputAsync(CancellationToken cancellationToken) =>
        new(_ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken));

    public ValueTask DisposeAsync()
    {
        try { _ws.Dispose(); }
        catch { /* already gone */ }
        try { _invoker?.Dispose(); }
        catch { /* already gone */ }
        return ValueTask.CompletedTask;
    }
}
