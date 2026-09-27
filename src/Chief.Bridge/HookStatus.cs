using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chief.Bridge;

/// <summary>
/// What a <c>hook</c> poller last said about itself, in <c>&lt;hook state&gt;.status</c> (default
/// <c>&lt;base&gt;/.hook.offset.status</c>). Written atomically. Never contains the URL or the key: a fire's
/// result is an HTTP status code or an error kind.
/// </summary>
internal sealed record HookStatus
{
    public const string Running = "running";
    public const string Stopped = "stopped";

    public int Pid { get; init; }
    public string State { get; init; } = Running;
    public long StartedAt { get; init; }
    public long HeartbeatAt { get; init; }
    public long? StoppedAt { get; init; }
    public double PollSeconds { get; init; }
    public double CooldownSeconds { get; init; }
    public int Trips { get; init; }
    public long? LastFireAt { get; init; }
    public string? LastResult { get; init; }
    public bool? LastOk { get; init; }
    public int? LastCount { get; init; }
    public long? LastOkAt { get; init; }
    public int Pending { get; init; }
    public int Failures { get; init; }
    public long? NextRetryAt { get; init; }
    public long FiredOk { get; init; }
    public long FiredFailed { get; init; }

    public static string PathFor(string statePath) => statePath + ".status";

    public JsonObject ToJson() => new()
    {
        ["pid"] = Pid,
        ["state"] = State,
        ["started_at"] = StartedAt,
        ["heartbeat_at"] = HeartbeatAt,
        ["stopped_at"] = StoppedAt,
        ["poll_s"] = PollSeconds,
        ["cooldown_s"] = CooldownSeconds,
        ["trips"] = Trips,
        ["last_fire_at"] = LastFireAt,
        ["last_result"] = LastResult,
        ["last_ok"] = LastOk,
        ["last_count"] = LastCount,
        ["last_ok_at"] = LastOkAt,
        ["pending"] = Pending,
        ["failures"] = Failures,
        ["next_retry_at"] = NextRetryAt,
        ["fired_ok"] = FiredOk,
        ["fired_failed"] = FiredFailed
    };

    public static HookStatus? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o)
                return null;
            if (Json.Str(o, "state") is not { } state || L(o, "pid") is not { } pid || L(o, "heartbeat_at") is not { } hb)
                return null;
            return new HookStatus
            {
                Pid = (int)pid, State = state, StartedAt = L(o, "started_at") ?? hb, HeartbeatAt = hb,
                StoppedAt = L(o, "stopped_at"), PollSeconds = D(o, "poll_s"), CooldownSeconds = D(o, "cooldown_s"),
                Trips = (int)(L(o, "trips") ?? 0), LastFireAt = L(o, "last_fire_at"), LastResult = Json.Str(o, "last_result"),
                LastOk = o["last_ok"] is JsonValue b && b.TryGetValue<bool>(out var ok) ? ok : null,
                LastCount = (int?)L(o, "last_count"), LastOkAt = L(o, "last_ok_at"), Pending = (int)(L(o, "pending") ?? 0),
                Failures = (int)(L(o, "failures") ?? 0), NextRetryAt = L(o, "next_retry_at"),
                FiredOk = L(o, "fired_ok") ?? 0, FiredFailed = L(o, "fired_failed") ?? 0
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static long? L(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
    private static double D(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
}

internal enum HookState
{
    /// <summary>No <c>hook</c> block in the config.</summary>
    NotConfigured,

    /// <summary>Configured, but no status file yet (the poller has never run with this state file).</summary>
    Unknown,

    /// <summary>Running, heartbeating, and the last fire (if any) succeeded.</summary>
    Running,

    /// <summary>Running, but the last fire failed; it is retrying. Wakes aren't getting through.</summary>
    Failing,

    /// <summary>Stopped, died, or hung: nothing is firing.</summary>
    NotRunning
}

internal sealed record HookView(HookState State, string Detail, HookStatus? Status)
{
    /// <summary>A heartbeat older than this (and never under 15 s) means the poller is gone or hung.</summary>
    public static TimeSpan StaleAfter(double pollSeconds) => TimeSpan.FromSeconds(Math.Max(15, pollSeconds * 3 + 5));

    public static HookView Classify(bool configured, HookStatus? s, DateTimeOffset now, Func<int, bool> isRunning)
    {
        if (!configured)
            return new HookView(HookState.NotConfigured, "not configured (no \"hook\" block in config.json)", null);
        if (s is null)
            return new HookView(HookState.Unknown, "unknown (no hook status file; the poller hasn't run)", null);

        var nowS = now.ToUnixTimeSeconds();
        string Ago(long t) => Human(Math.Max(0, nowS - t)) + " ago";

        if (s.State == HookStatus.Stopped)
            return new HookView(HookState.NotRunning, $"NOT RUNNING: stopped {Ago(s.StoppedAt ?? s.HeartbeatAt)} (pid {s.Pid})", s);
        if (!isRunning(s.Pid))
            return new HookView(HookState.NotRunning, $"NOT RUNNING: pid {s.Pid} died without a clean stop (last heartbeat {Ago(s.HeartbeatAt)})", s);
        if (nowS - s.HeartbeatAt > (long)StaleAfter(s.PollSeconds).TotalSeconds)
            return new HookView(HookState.NotRunning, $"NOT RUNNING: pid {s.Pid} is alive but its heartbeat is {Human(nowS - s.HeartbeatAt)} old (hung?)", s);

        var basics = $"pid {s.Pid}, heartbeat {Ago(s.HeartbeatAt)}, poll {s.PollSeconds:0.#}s, cooldown {s.CooldownSeconds:0.#}s, " +
                     (s.Trips > 0 ? $"{s.Trips} trusted trip(s)" : "all senders");
        if (s.Failures > 0)
        {
            var retry = s.NextRetryAt is { } r ? $", next retry in {Human(Math.Max(0, r - nowS))}" : "";
            return new HookView(HookState.Failing, $"FAILING: last {s.Failures} fire(s) failed (last: {s.LastResult}){retry}; {basics}", s);
        }

        return new HookView(HookState.Running, $"running ({basics})", s);
    }

    /// <summary>One line about the last fire, or null if it never fired.</summary>
    public static string? LastFireLine(HookStatus? s, DateTimeOffset now)
    {
        if (s?.LastFireAt is not { } at)
            return s is null ? null : $"hook last fire: never; {s.Pending} chat(s) pending; {s.FiredOk} ok / {s.FiredFailed} failed";
        var local = DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime();
        return $"hook last fire: {local:HH:mm:ss} ({Human(Math.Max(0, now.ToUnixTimeSeconds() - at))} ago): {s.LastResult}, {s.LastCount ?? 0} chat(s); " +
               $"{s.Pending} pending; {s.FiredOk} ok / {s.FiredFailed} failed since start";
    }

    public static string Human(long secs) =>
        secs < 90 ? $"{secs}s" : secs < 5400 ? $"{secs / 60}m" : $"{secs / 3600}h{secs % 3600 / 60:00}m";
}

/// <summary>Atomic writer for the poller's status file. A failed write warns once and never stops the poller.</summary>
internal sealed class HookStatusWriter(string path, TextWriter? err = null)
{
    private bool _warned;

    public string Path => path;

    public void Write(HookStatus s)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = $"{path}.tmp.{Environment.ProcessId}";
            File.WriteAllText(tmp, s.ToJson().ToJsonString(JsonUtil.Opts) + "\n");
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_warned)
                return;
            _warned = true;
            (err ?? Console.Error).WriteLine($"[chief] hook: warning: can't write status {path}: {ex.GetType().Name}");
        }
    }
}
