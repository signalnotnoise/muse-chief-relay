using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
/// <item>Deliver first, save second: the offset moves past chats only after each one was accepted (HTTP 2xx)
/// or copied into the retry file. Chats held by the cooldown stay on disk past the saved offset. A crash
/// between the 2xx and the save can send one piece twice; it does not drop a chat.</item>
/// <item>A reconnect replay can append a large backlog in one poll. That backlog is POSTed in pieces, oldest
/// first. Each piece is at most <c>max_batch</c> chats and at most <see cref="MaxPayloadBytes"/> UTF-8 bytes,
/// so one oversized body cannot come back HTTP 400 and stall the queue. A single chat that is still larger
/// is sent alone, not dropped. Pieces of one backlog go out in the same step, without waiting
/// <c>cooldown_s</c> between them. An empty piece is never sent.</item>
/// <item>At most one burst per <c>cooldown_s</c>. Chats that arrive inside it go out together in the next burst.
/// A piece that fails (non-2xx, timeout, network error) is written to the retry file and retried after
/// <c>max(cooldown_s, 1)</c>, doubling up to <c>max_retry_s</c>. An HTTP 400 on a piece that still holds more
/// than one chat is split and retried in the same step. A one-chat HTTP 400 cannot be split; that chat is
/// retried alone. A due retry is its own POST, so live chats in the same step are not part of that body and
/// are not copied into the retry file when the retry fails.</item>
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

    private readonly string _retryPath;
    private List<WatchedChat> _retry;
    private bool _retryUnreadable;

    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue;
    private int _retryFailures;
    private bool _warned;
    private HookStatus _s;

    /// <summary>
    /// Largest JSON body this poller will POST, in UTF-8 bytes. A live reconnect dump of about 83 chats
    /// came back HTTP 400 and then retried forever as one pile. 32 KiB stays under the usual 100 KiB JSON
    /// body limit (and under a 64 KiB proxy cap) that those receivers answer with 400. One chat that is
    /// still larger after <c>max_text</c> is sent alone.
    /// </summary>
    internal const int MaxPayloadBytes = 32 * 1024;

    /// <summary>Tests can tighten this. Production leaves it at <see cref="MaxPayloadBytes"/>.</summary>
    internal int PayloadByteLimit { get; set; } = MaxPayloadBytes;

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
        _retryPath = state + ".retry";
        _retry = LoadRetry();
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
        _log.WriteLine($"[chatbridge] hook: stopped{(_s.Pending > 0 ? $"; {_s.Pending} chat(s) not yet delivered stay queued for the next start" : "")}");
    }

    /// <summary>One poll: consume what doesn't qualify, and fire due chats in size-capped pieces.</summary>
    public async Task<HookStep> StepAsync(CancellationToken ct)
    {
        var poll = _watcher.Poll();
        if (_watcher.Warning is { } w && !_warned)
        {
            _warned = true;
            _log.WriteLine($"[chatbridge] hook: warning: {w}");
        }

        var fresh = poll.Chats.Where(Qualifies).ToList();
        var now = _clock();
        var retryDue = _retry.Count > 0 && now >= _retryNotBefore;
        var freshDue = fresh.Count > 0 && now >= _nextAllowed;

        if (!retryDue && !freshDue)
        {
            if (fresh.Count == 0)
                _watcher.Commit(poll);
            _s = _s with { Pending = _retry.Count + fresh.Count };
            return _retry.Count + fresh.Count > 0 ? HookStep.Held : HookStep.Idle;
        }

        // Retry chats are older, so they go first. They are a separate POST from chats that arrived
        // afterward: a 500 or a one-chat 400 on the retry must not pull those live chats into the retry file.
        var outcome = new DeliverOutcome();
        if (retryDue)
            outcome.Absorb(await DeliverAsync(_retry, ct));
        if (freshDue)
            outcome.Absorb(await DeliverAsync(fresh, ct));
        now = _clock();

        if (outcome.LastOk is null && outcome.LastFail is null)
        {
            // Nothing was posted. Leave the offset and the retry file where they are.
            _s = _s with { Pending = _retry.Count + fresh.Count };
            return _retry.Count + fresh.Count > 0 ? HookStep.Held : HookStep.Idle;
        }

        var kept = new List<WatchedChat>();
        if (!retryDue)
            kept.AddRange(_retry);
        kept.AddRange(outcome.Failed);
        SaveRetry(kept);
        _retry = kept;

        // Fresh chats are either delivered or copied into the retry file. Held chats stay past the offset.
        if (fresh.Count == 0 || freshDue)
            _watcher.Commit(poll);

        var nowS = now.ToUnixTimeSeconds();
        if (outcome.Failed.Count > 0)
        {
            _retryFailures++;
            var baseDelay = Math.Max(_h.CooldownSeconds, 1);
            var delay = Math.Min(_h.MaxRetrySeconds, baseDelay * Math.Pow(2, Math.Min(_retryFailures - 1, 30)));
            _retryNotBefore = now + TimeSpan.FromSeconds(delay);
        }
        else if (_retry.Count == 0)
        {
            _retryFailures = 0;
            _retryNotBefore = DateTimeOffset.MinValue;
        }

        // A retry that succeeds during the cooldown must not push that window out. Only a burst that
        // actually sent fresh chats starts the next gap.
        if (freshDue && outcome.OkPieces > 0)
            _nextAllowed = now + TimeSpan.FromSeconds(_h.CooldownSeconds);

        var failed = outcome.Failed.Count > 0;
        var last = failed ? outcome.LastFail! : outcome.LastOk!;
        _s = _s with
        {
            LastFireAt = nowS,
            LastResult = last.Result,
            LastOk = !failed,
            LastCount = failed ? outcome.Failed.Count : outcome.Delivered,
            LastOkAt = outcome.OkPieces > 0 ? nowS : _s.LastOkAt,
            Pending = _retry.Count + (freshDue ? 0 : fresh.Count),
            Failures = _retryFailures,
            NextRetryAt = _retry.Count > 0 ? _retryNotBefore.ToUnixTimeSeconds() : null,
            FiredOk = _s.FiredOk + (outcome.OkPieces > 0 && !failed ? 1 : 0),
            FiredFailed = _s.FiredFailed + (failed ? 1 : 0)
        };

        foreach (var piece in outcome.Leaves)
        {
            _log.WriteLine($"[chatbridge] hook: fired {piece.Count} chat(s) -> {piece.Result.Result}" +
                           (piece.Result.Ok || !failed ? "" : $"; retry in {(_retryNotBefore - now).TotalSeconds:0}s"));
        }

        Heartbeat(force: true);
        return failed ? HookStep.Failed : HookStep.Fired;
    }

    private bool Qualifies(WatchedChat c) =>
        _trips.Count == 0 || (PublicTrip.Canonical(c.Trip) is { } t && _trips.Contains(t));

    /// <summary>The payload: <c>{"source","channel","chats":[{nick,trip,text,ts}]}</c>. Text is cut to
    /// <c>max_text</c>. The caller splits a larger backlog into pieces; this does not drop chats.</summary>
    public JsonObject BuildPayload(IReadOnlyList<WatchedChat> chats)
    {
        var arr = new JsonArray();
        foreach (var c in chats)
        {
            var j = c.ToJson();
            if (c.Text is { } t && t.Length > _h.MaxText)
                j["text"] = t[.._h.MaxText];
            arr.Add(j);
        }

        return new JsonObject { ["source"] = _h.Source, ["channel"] = _cfg.Channel, ["chats"] = arr };
    }

    internal static int Utf8Bytes(JsonObject payload) =>
        Encoding.UTF8.GetByteCount(payload.ToJsonString(JsonUtil.Opts));

    /// <summary>Oldest first. A piece grows until one more chat would pass <c>max_batch</c> or
    /// <see cref="PayloadByteLimit"/>. A single chat that is already over the byte cap is its own piece.</summary>
    private List<List<WatchedChat>> SplitPieces(IReadOnlyList<WatchedChat> chats)
    {
        var pieces = new List<List<WatchedChat>>();
        var cur = new List<WatchedChat>();
        foreach (var chat in chats)
        {
            cur.Add(chat);
            if (cur.Count > 1 && (cur.Count > _h.MaxBatch || Utf8Bytes(BuildPayload(cur)) > PayloadByteLimit))
            {
                cur.RemoveAt(cur.Count - 1);
                pieces.Add(cur);
                cur = new List<WatchedChat> { chat };
            }
        }

        if (cur.Count > 0)
            pieces.Add(cur);
        return pieces;
    }

    private sealed class DeliverOutcome
    {
        public int Delivered;
        public int OkPieces;
        public FireResult? LastOk;
        public FireResult? LastFail;
        public List<WatchedChat> Failed { get; } = new();
        public List<(int Count, FireResult Result)> Leaves { get; } = new();

        public void Absorb(DeliverOutcome part)
        {
            Delivered += part.Delivered;
            OkPieces += part.OkPieces;
            if (part.LastOk is not null)
                LastOk = part.LastOk;
            if (part.LastFail is not null)
                LastFail = part.LastFail;
            Failed.AddRange(part.Failed);
            Leaves.AddRange(part.Leaves);
        }
    }

    /// <summary>POST every piece. An HTTP 400 that still contains more than one chat is split in this step
    /// so a body the receiver refuses for size does not pin the rest of the backlog. A one-chat 400 is
    /// stored for a later retry; it is not split into an empty piece and it is not posted again in this step.</summary>
    private async Task<DeliverOutcome> DeliverAsync(IReadOnlyList<WatchedChat> chats, CancellationToken ct)
    {
        var outcome = new DeliverOutcome();
        if (chats.Count == 0)
            return outcome;

        var pending = new LinkedList<List<WatchedChat>>();
        foreach (var piece in SplitPieces(chats))
        {
            if (piece.Count > 0)
                pending.AddLast(piece);
        }

        // Each split replaces one piece with two smaller ones. n chats need at most n - 1 splits to reach
        // singles. Past that, a 400 is retried whole instead of looping.
        var splitsLeft = chats.Count;
        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            var piece = node.Value;
            if (piece.Count == 0)
                continue;

            var result = await FireWhileHeartbeatingAsync(piece, ct);
            if (result.Ok)
            {
                outcome.Delivered += piece.Count;
                outcome.OkPieces++;
                outcome.LastOk = result;
                outcome.Leaves.Add((piece.Count, result));
                continue;
            }

            if (result.HttpStatus == 400 && splitsLeft > 0 && TryHalve(piece, out var older, out var newer))
            {
                splitsLeft--;
                _log.WriteLine($"[chatbridge] hook: HTTP 400 for {piece.Count} chat(s); splitting");
                pending.AddFirst(newer);
                pending.AddFirst(older);
                continue;
            }

            if (result.HttpStatus == 400)
                _log.WriteLine(piece.Count == 1
                    ? "[chatbridge] hook: HTTP 400 for 1 chat; cannot split, retrying alone"
                    : $"[chatbridge] hook: HTTP 400 for {piece.Count} chat(s); cannot split, retrying this piece");

            outcome.LastFail = result;
            outcome.Failed.AddRange(piece);
            outcome.Leaves.Add((piece.Count, result));
        }

        return outcome;
    }

    /// <summary>Older half then newer half, both non-empty, together the whole piece. One chat cannot be halved.</summary>
    private static bool TryHalve(List<WatchedChat> piece, out List<WatchedChat> older, out List<WatchedChat> newer)
    {
        older = new List<WatchedChat>();
        newer = new List<WatchedChat>();
        if (piece.Count < 2)
            return false;
        var mid = piece.Count / 2;
        if (mid <= 0 || mid >= piece.Count)
            return false;
        older = piece.GetRange(0, mid);
        newer = piece.GetRange(mid, piece.Count - mid);
        return older.Count > 0 && newer.Count > 0 && older.Count + newer.Count == piece.Count;
    }

    private List<WatchedChat> LoadRetry()
    {
        var list = new List<WatchedChat>();
        if (!File.Exists(_retryPath))
            return list;
        try
        {
            foreach (var line in File.ReadLines(_retryPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (JsonNode.Parse(line) is not JsonObject o)
                    throw new JsonException("retry line is not an object");
                list.Add(new WatchedChat(Json.Str(o, "nick"), Json.Str(o, "trip"), Json.Str(o, "text"), o["ts"]?.DeepClone()));
            }

            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Leave the file in place. SaveRetry refuses to replace it, and the offset is not moved
            // on this poll, so a later start can still send the chats.
            _retryUnreadable = true;
            _log.WriteLine($"[chatbridge] hook: warning: retry file unreadable ({ex.GetType().Name}); leaving it in place");
            return new List<WatchedChat>();
        }
    }

    private void SaveRetry(IReadOnlyList<WatchedChat> chats)
    {
        if (_retryUnreadable)
            throw new IOException("retry file unreadable");

        if (chats.Count == 0)
        {
            if (File.Exists(_retryPath))
                File.Delete(_retryPath);
            return;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(_retryPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = $"{_retryPath}.tmp.{Environment.ProcessId}";
        var sb = new StringBuilder();
        foreach (var c in chats)
            sb.Append(c.ToJson().ToJsonString(JsonUtil.Opts)).Append('\n');
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, _retryPath, overwrite: true);
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
