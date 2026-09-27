using System.Net;
using System.Text.Json.Serialization;

namespace Chief.Bridge;

/// <summary>
/// <c>hook</c> block of <c>config.json</c>: settings for <c>Chief.Bridge hook</c>, which POSTs new inbound chats
/// to a webhook so a woken-on-request agent gets called. The URL and key are never in the config: only the
/// names of the environment variables that hold them.
/// </summary>
internal sealed class HookConfig
{
    /// <summary>Environment variable holding the webhook URL (https, or http to a loopback address only).</summary>
    [JsonPropertyName("url_env")] public string UrlEnv { get; set; } = "CHIEF_HOOK_URL";

    /// <summary>Environment variable holding the bare key. Empty means no Authorization header.</summary>
    [JsonPropertyName("auth_env")] public string? AuthEnv { get; set; } = "CHIEF_HOOK_AUTH";

    /// <summary>Put in front of the key: <c>Authorization: &lt;scheme&gt; &lt;key&gt;</c>. Empty sends the bare key.</summary>
    [JsonPropertyName("auth_scheme")] public string AuthScheme { get; set; } = "Bearer";

    /// <summary>Fallback poll interval. File-system events usually wake the poller sooner.</summary>
    [JsonPropertyName("poll_s")] public double PollSeconds { get; set; } = 5;

    /// <summary>Minimum gap between fires. Chats that arrive inside it go out together in the next fire.</summary>
    [JsonPropertyName("cooldown_s")] public double CooldownSeconds { get; set; } = 15;

    /// <summary>After a failed fire the wait doubles from <c>max(cooldown_s, 1)</c> up to this.</summary>
    [JsonPropertyName("max_retry_s")] public double MaxRetrySeconds { get; set; } = 120;

    /// <summary>Per-request timeout.</summary>
    [JsonPropertyName("timeout_s")] public double TimeoutSeconds { get; set; } = 15;

    /// <summary>Only chats from these trips fire the hook (and only they are sent). Empty: every sender except
    /// the bridge's own nick.</summary>
    public List<string> Trips { get; set; } = new();

    /// <summary>Offset file, relative to the base dir. Its status file is this plus <c>.status</c>.</summary>
    public string State { get; set; } = ".hook.offset";

    /// <summary><c>source</c> field of the payload.</summary>
    public string Source { get; set; } = "chief-bridge-hook";

    /// <summary>Longer chat texts are cut to this many characters in the payload.</summary>
    [JsonPropertyName("max_text")] public int MaxText { get; set; } = 2000;

    /// <summary>At most this many chats per fire (the newest); the rest are counted in <c>omitted</c>.</summary>
    [JsonPropertyName("max_batch")] public int MaxBatch { get; set; } = 50;

    public void Validate(string configPath)
    {
        Trips = (Trips ?? new()).Select(t => (t ?? "").Trim().TrimStart('!')).Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        string E(string msg) => $"{configPath}: hook.{msg}";

        if (string.IsNullOrWhiteSpace(UrlEnv))
            throw new ConfigException(E("url_env is required (the name of the variable holding the URL)"));
        if (!Range(PollSeconds, 0.1, 300))
            throw new ConfigException(E("poll_s must be from 0.1 to 300"));
        if (!Range(CooldownSeconds, 0, 3600))
            throw new ConfigException(E("cooldown_s must be from 0 to 3600"));
        if (!Range(MaxRetrySeconds, 1, 86400))
            throw new ConfigException(E("max_retry_s must be from 1 to 86400"));
        if (!Range(TimeoutSeconds, 1, 300))
            throw new ConfigException(E("timeout_s must be from 1 to 300"));
        if (MaxText < 1 || MaxBatch < 1)
            throw new ConfigException(E("max_text and max_batch must be at least 1"));
        if (string.IsNullOrWhiteSpace(State))
            throw new ConfigException(E("state must not be empty"));
        if (AuthScheme is null || AuthScheme.Any(char.IsWhiteSpace))
            throw new ConfigException(E("auth_scheme must be one word (or empty)"));
        Source ??= "chief-bridge-hook";
    }

    private static bool Range(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;

    public string StatePath(string baseDir) => Path.GetFullPath(State, baseDir);
}

/// <summary>The webhook URL and Authorization value, read from the environment. <see cref="ToString"/> never
/// shows either, and error messages name the variable, never its value.</summary>
internal sealed class HookSecrets
{
    public Uri Url { get; }
    public string? Authorization { get; }

    private HookSecrets(Uri url, string? authorization)
    {
        Url = url;
        Authorization = authorization;
    }

    public override string ToString() => "HookSecrets(<redacted>)";

    public static HookSecrets FromEnvironment(HookConfig h, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var rawUrl = getEnv(h.UrlEnv)?.Trim();
        if (string.IsNullOrEmpty(rawUrl))
            throw new ConfigException($"hook: environment variable {h.UrlEnv} is not set");
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new ConfigException($"hook: {h.UrlEnv} is not an absolute http(s) URL");
        if (url.Scheme == Uri.UriSchemeHttp && !IsLoopback(url))
            throw new ConfigException($"hook: {h.UrlEnv} uses plain http to a non-loopback host; the key would travel unencrypted. Use https");

        string? auth = null;
        if (!string.IsNullOrWhiteSpace(h.AuthEnv))
        {
            var key = getEnv(h.AuthEnv)?.Trim();
            if (string.IsNullOrEmpty(key))
                throw new ConfigException($"hook: environment variable {h.AuthEnv} is not set (set auth_env to \"\" for no Authorization header)");
            if (key.Any(c => char.IsControl(c)))
                throw new ConfigException($"hook: {h.AuthEnv} contains control characters");
            auth = string.IsNullOrEmpty(h.AuthScheme) ? key : $"{h.AuthScheme} {key}";
        }

        return new HookSecrets(url, auth);
    }

    private static bool IsLoopback(Uri u) =>
        u.IsLoopback || (IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>Every string that must never appear in output: the URL (and its pieces that could identify the
    /// endpoint) and the key.</summary>
    public IEnumerable<string> SecretValues()
    {
        yield return Url.OriginalString;
        yield return Url.AbsoluteUri;
        if (Url.PathAndQuery.Length > 1)
            yield return Url.PathAndQuery;
        if (Authorization is not null)
        {
            yield return Authorization;
            var sp = Authorization.IndexOf(' ');
            if (sp >= 0)
                yield return Authorization[(sp + 1)..];
        }
    }
}
