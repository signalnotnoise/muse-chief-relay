using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChatBridge;

/// <summary>
/// <c>auto_ack</c> block of <c>config.json</c>. Off unless <c>enabled</c> is true. See README, "Auto-acknowledgement".
/// </summary>
internal sealed class AutoAckConfig
{
    public const double MinCooldownSeconds = 10;
    public const int MaxTextLength = 300;

    public bool Enabled { get; set; }

    /// <summary>Trips whose messages are acknowledged when they mention the bridge's nick or give it a task
    /// (humans, e.g. Alex).</summary>
    [JsonPropertyName("mention_trips")] public List<string> MentionTrips { get; set; } = new();

    /// <summary>Trips whose messages are acknowledged only when they are a task addressed to the bridge's nick
    /// (other agents, e.g. Fuse). Plain chat from these trips never triggers an ack, so agent chatter can't
    /// start a loop.</summary>
    [JsonPropertyName("task_trips")] public List<string> TaskTrips { get; set; } = new();

    /// <summary>At most one ack per this many seconds.</summary>
    [JsonPropertyName("cooldown_s")] public double CooldownSeconds { get; set; } = 60;

    /// <summary>At most this many acks in any rolling hour.</summary>
    [JsonPropertyName("max_per_hour")] public int MaxPerHour { get; set; } = 20;

    /// <summary>Sent normally. <c>{from}</c> is the sender's nick.</summary>
    public string Text { get; set; } = "(auto) got it, thinking…";

    /// <summary>Sent for a task. <c>{id}</c> is the task id (or "?"), <c>{from}</c> the sender's nick.</summary>
    [JsonPropertyName("task_text")] public string TaskText { get; set; } = "(auto) got task {id}, thinking…";

    /// <summary>Sent instead when the hook poller's status says it is not running or its fires are failing, so the
    /// ack never promises a reply that nothing is going to wake chief for.</summary>
    [JsonPropertyName("offline_text")] public string OfflineText { get; set; } =
        "(auto) got it, but the wake-up hook isn't working right now, so the reply may be late";

    /// <summary>Normalise trips (trim, drop a leading '!') and reject a config that could spam.</summary>
    public void Validate(string configPath)
    {
        MentionTrips = Normalise(MentionTrips);
        TaskTrips = Normalise(TaskTrips);
        if (!Enabled)
            return;

        if (MentionTrips.Count == 0 && TaskTrips.Count == 0)
            throw new ConfigException($"{configPath}: auto_ack is enabled but mention_trips and task_trips are both empty");
        if (!double.IsFinite(CooldownSeconds) || CooldownSeconds < MinCooldownSeconds)
            throw new ConfigException($"{configPath}: auto_ack.cooldown_s must be at least {MinCooldownSeconds:0}");
        if (MaxPerHour < 1)
            throw new ConfigException($"{configPath}: auto_ack.max_per_hour must be at least 1");
        foreach (var (name, value) in new[] { ("text", Text), ("task_text", TaskText), ("offline_text", OfflineText) })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTextLength)
                throw new ConfigException($"{configPath}: auto_ack.{name} must be 1 to {MaxTextLength} characters");
        }
    }

    private static List<string> Normalise(List<string>? trips) =>
        (trips ?? new()).Select(t => (t ?? "").Trim().TrimStart('!')).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>Why a message would be acknowledged.</summary>
internal sealed record AckTrigger(bool IsTask, string? TaskId);

internal static class Addressing
{
    /// <summary>
    /// Is <paramref name="text"/> addressed to <paramref name="nick"/>?
    /// <list type="bullet">
    /// <item>A protocol JSON line (a whole-message object with a string <c>type</c>) counts only if it is
    /// <c>"type":"task"</c> with <c>"to"</c> equal to the nick (case-insensitive). Acks, results, opinions, pings
    /// and tasks for someone else never count, even if they mention the nick.</item>
    /// <item>The shortcut <c>TASK to &lt;nick&gt;: …</c> is a task without an id.</item>
    /// <item>Otherwise, the nick as a whole word, optionally with <c>@</c>, is a mention. "chief's" and
    /// "@chief," count; "chiefly", "mischief" and "Chief.Bridge" don't.</item>
    /// </list>
    /// </summary>
    public static AckTrigger? Detect(string? text, string nick)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(nick))
            return null;
        var t = text.Trim();

        if (t.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(t) is JsonObject o && Json.Str(o, "type") is { } type)
                {
                    if (type == "task" && string.Equals(Json.Str(o, "to"), nick, StringComparison.OrdinalIgnoreCase))
                        return new AckTrigger(true, IdOf(o));
                    return null;
                }
            }
            catch (JsonException)
            {
                // not JSON: plain text rules below
            }
        }

        var n = Regex.Escape(nick);
        if (Regex.IsMatch(t, $@"^TASK\s+to\s+@?{n}\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return new AckTrigger(true, null);
        if (Regex.IsMatch(t, $@"(?<![\p{{L}}\p{{N}}_\-.@])@?{n}(?![\p{{L}}\p{{N}}_\-])(?!\.[\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return new AckTrigger(false, null);
        return null;
    }

    private static string? IdOf(JsonObject o) => o["id"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };
}

internal sealed record AckDecision(bool Send, string Reason, string? Text = null, AckTrigger? Trigger = null)
{
    /// <summary>A trusted sender addressed us but the ack was held back (cooldown, hourly cap). Worth logging.</summary>
    public bool Suppressed => !Send && Trigger is not null;
}

/// <summary>
/// Decides whether an inbound chat gets an instant acknowledgement from the bridge, and with what text.
/// Pure apart from its own rate-limit memory; the caller supplies the clock and the listener state.
/// <list type="bullet">
/// <item>Never for the bridge's own nick (any case) or its own trip (from <c>onlineSet</c>).</item>
/// <item>Never for an untripped sender, or a trip on neither list. Nicks are not identity.</item>
/// <item><c>mention_trips</c>: a mention or a task. <c>task_trips</c>: a task only.</item>
/// <item>One ack per <c>cooldown_s</c> and at most <c>max_per_hour</c>, across all senders.</item>
/// </list>
/// </summary>
internal sealed class AutoAcker
{
    private readonly AutoAckConfig _cfg;
    private readonly string _ownNick;
    private readonly HashSet<string> _mention;
    private readonly HashSet<string> _task;
    private readonly Queue<DateTimeOffset> _recent = new();
    private DateTimeOffset? _last;
    private string? _ownTrip;

    public AutoAcker(AutoAckConfig cfg, string ownNick)
    {
        _cfg = cfg;
        _ownNick = ownNick;
        _mention = new HashSet<string>(cfg.MentionTrips, StringComparer.Ordinal);
        _task = new HashSet<string>(cfg.TaskTrips, StringComparer.Ordinal);
    }

    /// <summary>
    /// The bridge's own trip, learned from <c>onlineSet</c> or v1 <c>welcome</c>.
    /// Stored without a leading <c>!</c>. Messages carrying it are never acked.
    /// </summary>
    public string? OwnTrip
    {
        get => _ownTrip;
        set => _ownTrip = PublicTrip.Canonical(value);
    }

    public AckDecision Consider(string? nick, string? trip, string? text, DateTimeOffset now, Func<HookView> hook)
    {
        if (!_cfg.Enabled)
            return new AckDecision(false, "disabled");
        if (string.IsNullOrEmpty(nick) || string.Equals(nick, _ownNick, StringComparison.OrdinalIgnoreCase))
            return new AckDecision(false, "own nick");
        var tripId = PublicTrip.Canonical(trip);
        if (tripId is null)
            return new AckDecision(false, "untripped");
        if (_ownTrip is not null && tripId == _ownTrip)
            return new AckDecision(false, "own trip");

        var mentionOk = _mention.Contains(tripId);
        if (!mentionOk && !_task.Contains(tripId))
            return new AckDecision(false, "untrusted trip");

        var trigger = Addressing.Detect(text, _ownNick);
        if (trigger is null)
            return new AckDecision(false, "not addressed");
        if (!trigger.IsTask && !mentionOk)
            return new AckDecision(false, "mention from a task-only trip");

        if (_last is { } last && now - last < TimeSpan.FromSeconds(_cfg.CooldownSeconds))
            return new AckDecision(false, "cooldown", Trigger: trigger);
        while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromHours(1))
            _recent.Dequeue();
        if (_recent.Count >= _cfg.MaxPerHour)
            return new AckDecision(false, "hourly cap", Trigger: trigger);

        var view = hook();
        var template = view.State is HookState.NotRunning or HookState.Failing ? _cfg.OfflineText
            : trigger.IsTask ? _cfg.TaskText
            : _cfg.Text;
        var textOut = template
            .Replace("{id}", Clean(trigger.TaskId) ?? "?", StringComparison.Ordinal)
            .Replace("{from}", Clean(nick) ?? "?", StringComparison.Ordinal);

        _last = now;
        _recent.Enqueue(now);
        return new AckDecision(true, $"{(trigger.IsTask ? "task" : "mention")}; hook {view.State.ToString().ToLowerInvariant()}", textOut, trigger);
    }

    /// <summary>Chat-supplied values go back into the channel: keep them short and on one line.</summary>
    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var one = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return one.Length == 0 ? null : one.Length > 40 ? one[..40] + "…" : one;
    }
}
