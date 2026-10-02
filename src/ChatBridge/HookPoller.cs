using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ChatBridge;

/// <summary>Outcome of one POST. <see cref="Result"/> is safe to log: an HTTP status or an error kind, never a
/// message that could echo the URL.</summary>
internal sealed record FireResult(bool Ok, int? HttpStatus, string Result);

/// <summary>What one <see cref="HookPoller.StepAsync"/> did.</summary>
internal enum HookStep
{
    Idle,
    Held,
    Fired,
    Failed
}

/// <summary>
/// <c>Chief.Bridge hook</c>: watches <c>inbox.jsonl</c> and POSTs new inbound chats to a webhook, so an agent
/// that only runs when called gets called.
/// <list type="bullet">
/// <item>Reading uses <see cref="InboxWatcher"/> with its own offset file (default <c>&lt;base&gt;/.hook.offset</c>):
/// byte offset on a line boundary, partial lines left for later, truncation and rotation detected, first run
/// starts at the end of the inbox. The bridge's own nick is skipped; with <c>trips</c> set, so is every
/// other sender whose trip isn't listed.</item>
/// <item>Deliver first, save second: the offset moves past a batch only after a 2xx. Chats held by the cooldown
/// or a failed fire stay on disk past the saved offset, so a restart or crash re-sends them instead of losing
/// them (a crash between the 2xx and the save can send one batch twice).</item>
/// <item>At most one fire per <c>cooldown_s</c>. Chats that arrive inside it go out together in the next fire.
/// A failed fire (non-2xx, timeout, network error) is retried after <c>max(cooldown_s, 1)</c>, doubling up to
/// <c>max_retry_s</c>.</item>
/// <item>Wakes on file-system events, with a poll every <c>poll_s</c> as the fallback.</item>
/// </list>
/// </summary>
internal sealed class HookPoller : IDisposable
{
    private readonly RelayConfig _cfg;
    private readonly HookConfig _h;
    private readonly HookSecrets _secrets;
    private readonly HttpClient _http;
    private readonly InboxWatcher _watcher;
    private readonly HashSet<string> _trips;
    private readonly HookStatusWriter _status;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TextWriter _log;
    private readonly string _inbox;

    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private bool _warned;
    private HookStatus _s;

    public HookPoller(RelayConfig cfg, HookSecrets secrets, HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? clock = null, TextWriter? log = null)
    {
        _cfg = cfg;
        _h = cfg.Hook ?? throw new ConfigException($"{cfg.ConfigPath}: no \"hook\" block");
        _secrets = secrets;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? Console.Out;
        _inbox = Path.Combine(cfg.BaseDir, "inbox.jsonl");
        var state = _h.StatePath(cfg.BaseDir);
        _watcher = new InboxWatcher(_inbox, state, cfg.Nick);
        _status = new HookStatusWriter(HookStatus.PathFor(state), _log);
        _trips = new HashSet<string>(_h.Trips, StringComparer.Ordinal);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(_h.TimeoutSeconds)
        };
        var now = _clock().ToUnixTimeSeconds();
        _s = new HookStatus
        {
            Pid = Environment.ProcessId, StartedAt = now, HeartbeatAt = now, PollSeconds = _h.PollSeconds,
            CooldownSeconds = _h.CooldownSeconds, Trips = _trips.Count
        };
    }

    public string StatusPath => _status.Path;
    public HookStatus Status => _s;

    public void Dispose() => _http.Dispose();

    /// <summary>Run until <paramref name="ct"/> is cancelled, then write <c>stopped</c>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _log.WriteLine($"[chatbridge] hook: watching {_inbox} as {_cfg.Nick}; poll {_h.PollSeconds:0.#}s, cooldown {_h.CooldownSeconds:0.#}s, " +
                       $"{(_trips.Count > 0 ? $"{_trips.Count} trusted trip(s)" : "all senders")}; URL from ${_h.UrlEnv}");
        Heartbeat();
        using var changed = new SemaphoreSlim(0, 1);
        using var fsw = InboxWatcher.WatchFile(_inbox, changed);
        var poll = TimeSpan.FromSeconds(_h.PollSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.WriteLine($"[chatbridge] hook: inbox read failed ({ex.GetType().Name}); retrying");
            }

            Heartbeat();
            var wait = poll;
            if (_s.Pending > 0)
            {
                var untilAllowed = _nextAllowed - _clock();
                if (untilAllowed < wait)
                    wait = untilAllowed < TimeSpan.Zero ? TimeSpan.Zero : untilAllowed;
            }

            try
            {
                if (wait > TimeSpan.Zero)
                    await changed.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var now = _clock().ToUnixTimeSeconds();
        _s = _s with { State = HookStatus.Stopped, StoppedAt = now, HeartbeatAt = now };
        _status.Write(_s);
        _log.WriteLine($"[chatbridge] hook: stopped{(_s.Pending > 0 ? $"; {_s.Pending} chat(s) not yet delivered stay queued in the inbox for the next start" : "")}");
    }

    /// <summary>One poll: consume what doesn't qualify, and fire the qualifying chats if the cooldown allows.</summary>
    public async Task<HookStep> StepAsync(CancellationToken ct)
    {
        var poll = _watcher.Poll();
        if (_watcher.Warning is { } w && !_warned)
        {
            _warned = true;
            _log.WriteLine($"[chatbridge] hook: warning: {w}");
        }

        var chats = poll.Chats.Where(Qualifies).ToList();
        if (chats.Count == 0)
        {
            _watcher.Commit(poll);
            _s = _s with { Pending = 0 };
            return HookStep.Idle;
        }

        _s = _s with { Pending = chats.Count };
        var now = _clock();
        if (now < _nextAllowed)
            return HookStep.Held;

        var result = await FireWhileHeartbeatingAsync(chats, ct);
        now = _clock();
        var nowS = now.ToUnixTimeSeconds();
        if (result.Ok)
        {
            _watcher.Commit(poll);
            _nextAllowed = now + TimeSpan.FromSeconds(_h.CooldownSeconds);
            _s = _s with
            {
                LastFireAt = nowS, LastResult = result.Result, LastOk = true, LastCount = chats.Count, LastOkAt = nowS,
                Pending = 0, Failures = 0, NextRetryAt = null, FiredOk = _s.FiredOk + 1
            };
        }
        else
        {
            var failures = _s.Failures + 1;
            var baseDelay = Math.Max(_h.CooldownSeconds, 1);
            var delay = Math.Min(_h.MaxRetrySeconds, baseDelay * Math.Pow(2, Math.Min(failures - 1, 30)));
            _nextAllowed = now + TimeSpan.FromSeconds(delay);
            _s = _s with
            {
                LastFireAt = nowS, LastResult = result.Result, LastOk = false, LastCount = chats.Count,
                Failures = failures, NextRetryAt = _nextAllowed.ToUnixTimeSeconds(), FiredFailed = _s.FiredFailed + 1
            };
        }

        _log.WriteLine($"[chatbridge] hook: fired {chats.Count} chat(s) -> {result.Result}" +
                       (result.Ok ? "" : $"; retry in {(_nextAllowed - now).TotalSeconds:0}s"));
        Heartbeat(force: true);
        return result.Ok ? HookStep.Fired : HookStep.Failed;
    }

    private bool Qualifies(WatchedChat c) =>
        _trips.Count == 0 || (PublicTrip.Canonical(c.Trip) is { } t && _trips.Contains(t));

    /// <summary>The payload: <c>{"source","channel","chats":[{nick,trip,text,ts}]}</c>, plus <c>"omitted": n</c>
    /// when more than <c>max_batch</c> chats were waiting (the newest are sent).</summary>
    public JsonObject BuildPayload(IReadOnlyList<WatchedChat> chats)
    {
        var send = chats.Count > _h.MaxBatch ? chats.Skip(chats.Count - _h.MaxBatch).ToList() : chats;
        var arr = new JsonArray();
        foreach (var c in send)
        {
            var j = c.ToJson();
            if (c.Text is { } t && t.Length > _h.MaxText)
                j["text"] = t[.._h.MaxText];
            arr.Add(j);
        }

        var o = new JsonObject { ["source"] = _h.Source, ["channel"] = _cfg.Channel, ["chats"] = arr };
        if (chats.Count > send.Count)
            o["omitted"] = chats.Count - send.Count;
        return o;
    }

    /// <summary>
    /// How long to wait between heartbeats while <see cref="FireAsync"/> is still awaiting the webhook.
    /// A request may run for the whole <c>timeout_s</c> (up to 300 s). The stale threshold is only
    /// <c>max(15 s, 3 × poll_s + 5 s)</c>, so without this pulse <c>status</c> and auto-ack call a live
    /// poller NOT RUNNING and a second process is willing to start. Tests replace the wait.
    /// </summary>
    internal Func<CancellationToken, Task> HeartbeatWait { get; set; } =
        ct => Task.Delay(HeartbeatInterval, ct);

    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>POST, and keep the status heartbeat fresh for as long as the request is in flight.</summary>
    private async Task<FireResult> FireWhileHeartbeatingAsync(IReadOnlyList<WatchedChat> chats, CancellationToken ct)
    {
        var firing = FireAsync(chats, ct);
        while (!firing.IsCompleted)
        {
            Task pulse;
            try
            {
                pulse = HeartbeatWait(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var done = await Task.WhenAny(firing, pulse).ConfigureAwait(false);
            if (done == firing || ct.IsCancellationRequested || !pulse.IsCompletedSuccessfully)
                break;
            Heartbeat(force: true);
        }

        return await firing.ConfigureAwait(false);
    }

    public async Task<FireResult> FireAsync(IReadOnlyList<WatchedChat> chats, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _secrets.Url)
        {
            Content = new StringContent(BuildPayload(chats).ToJsonString(JsonUtil.Opts), Encoding.UTF8, "application/json")
        };
        if (_secrets.Authorization is { } auth)
            req.Headers.TryAddWithoutValidation("Authorization", auth);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Chief.Bridge-hook", "1"));

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;
            return new FireResult(code is >= 200 and < 300, code, $"HTTP {code}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new FireResult(false, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            // Only the error kind: exception messages can contain the host or URL.
            return new FireResult(false, null, $"error {ex.HttpRequestError}");
        }
    }

    private long _lastBeat;

    private void Heartbeat(bool force = false)
    {
        var now = _clock().ToUnixTimeSeconds();
        if (!force && now - _lastBeat < 5 && _lastBeat != 0)
            return;
        _lastBeat = now;
        _s = _s with { HeartbeatAt = now };
        _status.Write(_s);
    }
}

/// <summary>Options for <c>hook</c>, after the global <c>--config</c> has been taken out.</summary>
internal sealed record HookOptions(bool Test)
{
    public const int ExitAlreadyRunning = 4;
    public const int ExitTestFailed = 5;

    public static HookOptions Parse(IReadOnlyList<string> args)
    {
        var test = false;
        foreach (var a in args)
        {
            if (a == "--test")
                test = true;
            else
                throw new ArgumentException($"hook: unknown argument '{a}'");
        }

        return new HookOptions(test);
    }
}

/// <summary>
/// Exclusive lock held for the life of one <c>hook</c> process, one per offset file
/// (<c>&lt;state&gt;.lock</c>). The status file is only a report: two processes can both read "not running"
/// before either writes it. The kernel drops this lock when the process exits, including a crash.
/// </summary>
internal sealed class HookInstanceLock : IDisposable
{
    private readonly FileStream _fs;

    private HookInstanceLock(FileStream fs) => _fs = fs;

    public static string PathFor(string statePath) => statePath + ".lock";

    /// <summary>Null when another process (or this one) already holds the lock.</summary>
    public static HookInstanceLock? TryAcquire(string path)
    {
        FileStream? fs = null;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // FileShare.None is the lock. A second open fails until this stream is disposed.
            fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new HookInstanceLock(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fs?.Dispose();
            return null;
        }
    }

    public void Dispose() => _fs.Dispose();
}
