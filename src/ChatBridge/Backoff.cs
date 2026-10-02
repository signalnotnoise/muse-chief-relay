namespace ChatBridge;

/// <summary>
/// Reconnect delay: 1 s doubling to a 30 s cap. It goes back to 1 s after any session that was
/// confirmed by the server (onlineSet) or stayed up for at least a minute, so a bridge that has been
/// running for days still reconnects quickly after an ordinary drop.
/// The bridge multiplies the value by a factor in [0.8, 1.2] (<see cref="WithJitter"/>) and still
/// never waits longer than <see cref="MaxSeconds"/>.
/// </summary>
internal sealed class Backoff
{
    public static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);
    public const int InitialSeconds = 1;
    public const int MaxSeconds = 30;

    private int _next = InitialSeconds;

    public int NextDelaySeconds(bool sessionConfirmed, TimeSpan uptime)
    {
        if (sessionConfirmed || uptime >= StableUptime)
            _next = InitialSeconds;
        var delay = _next;
        _next = Math.Min(_next * 2, MaxSeconds);
        return delay;
    }

    /// <summary>
    /// <paramref name="unit"/> is in [0, 1]. 0 sleeps 80% of <paramref name="baseSeconds"/>, 1 sleeps
    /// 120%, and the result never exceeds <see cref="MaxSeconds"/>.
    /// </summary>
    public static TimeSpan WithJitter(int baseSeconds, double unit)
    {
        if (double.IsNaN(unit) || unit < 0)
            unit = 0;
        else if (unit > 1)
            unit = 1;
        var seconds = Math.Min(MaxSeconds, baseSeconds * (0.8 + unit * 0.4));
        var ms = Math.Max(0, (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));
        return TimeSpan.FromMilliseconds(ms);
    }
}
