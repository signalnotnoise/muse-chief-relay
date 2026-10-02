using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatBridge;

internal sealed class ConfigException(string message) : Exception(message);

internal sealed class RelayConfig
{
    public string Url { get; set; } = "wss://hack.chat/chat-ws";
    public string Origin { get; set; } = "https://hack.chat";
    public string Channel { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Base { get; set; } = ".";

    // Optional hack.chat password; gives this nick a tripcode. Sent only in the join frame, never logged.
    public string? Pass { get; set; }

    // Optional public trip code for voizle-text-relay (Ab12Cd or !Ab12Cd). Not a password.
    // Sent only on a v1 join, as ! plus the six-character code. Safe to log.
    public string? Trip { get; set; }

    /// <summary>
    /// True when <see cref="Url"/> is not hack.chat. Those sockets speak voizle-text-relay v1
    /// (wait for hello, then join with room/type). hack.chat keeps the cmd/channel join.
    /// </summary>
    [JsonIgnore]
    public bool SpeaksVoizle => RelayUrl.SpeaksVoizle(Url);

    // Optional instant acknowledgement from the bridge when a trusted trip addresses it. Off by default.
    [JsonPropertyName("auto_ack")] public AutoAckConfig AutoAck { get; set; } = new();

    // Optional webhook poller settings for `hook`. Null: not configured.
    public HookConfig? Hook { get; set; }

    /// <summary>Agents this process routes mentions to. Empty or omitted: no per-agent inboxes. Never implied.</summary>
    public List<AgentConfig>? Agents { get; set; }

    /// <summary>Mention routing. Off unless <c>enabled</c> is true, so existing inbox.jsonl bridges stay as they are.</summary>
    public MentionConfig? Mentions { get; set; }

    /// <summary>Default <see cref="ReceiveIdleSeconds"/>: end a joined session after 300s with no inbound frame.</summary>
    public const double DefaultReceiveIdleSeconds = 300;

    /// <summary>Upper bound for <see cref="ReceiveIdleSeconds"/> (one day). 0 is allowed and disables the watchdog.</summary>
    public const double MaxReceiveIdleSeconds = 86400;

    /// <summary>
    /// External process watchdogs should wait this long when <see cref="ReceiveIdleSeconds"/> is left at
    /// <see cref="DefaultReceiveIdleSeconds"/>. Longer than the bridge, so the process can rejoin itself
    /// before anything else kills it, and the two clocks do not fight.
    /// </summary>
    public const int RecommendedExternalWatchdogSeconds = 360;

    /// <summary>
    /// Seconds without any inbound server frame before a joined session is ended and reconnected.
    /// Default 300. <c>0</c> disables the watchdog. Negative, non-finite, and values above
    /// <see cref="MaxReceiveIdleSeconds"/> are a bad config (exit 2).
    /// Armed only after <c>onlineSet</c>, and disarmed for the whole reconnect backoff, so the timer
    /// cannot fire or pile reconnects during a maintenance window. Any external process watchdog should
    /// use a longer clock — <see cref="RecommendedExternalWatchdogSeconds"/> seconds when this stays 300 —
    /// so the bridge gets the first chance to rejoin and the two do not fight.
    /// </summary>
    [JsonPropertyName("receive_idle_s")]
    public double ReceiveIdleSeconds { get; set; } = DefaultReceiveIdleSeconds;

    /// <summary><see cref="ReceiveIdleSeconds"/> as a timeout. Zero when the watchdog is disabled.</summary>
    [JsonIgnore]
    public TimeSpan ReceiveIdle =>
        ReceiveIdleSeconds <= 0 || !double.IsFinite(ReceiveIdleSeconds)
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(ReceiveIdleSeconds);

    [JsonIgnore] public string BaseDir { get; set; } = ".";
    [JsonIgnore] public string ConfigPath { get; set; } = "";
    [JsonIgnore] public string Source { get; set; } = "";
    [JsonIgnore] public AgentDirectory Roster { get; set; } = AgentDirectory.Empty;
    [JsonIgnore] public MentionConfig MentionRouting { get; set; } = new();

    /// <summary>
    /// Resolve and load the config.
    /// <list type="number">
    /// <item><paramref name="explicitPath"/> (--config or the bridge's first argument)</item>
    /// <item><see cref="ProductInfo.LegacyConfigEnv"/>, then <see cref="ProductInfo.ConfigEnv"/></item>
    /// <item>./config.json in the working directory</item>
    /// <item>./config.example.json, only when <paramref name="allowExampleFallback"/> (the bridge run)</item>
    /// </list>
    /// An explicit path or MUSE_RELAY_CONFIG that points at a missing file is an error. There is no
    /// search of parent directories or of the app directory, so a stray config.json elsewhere can never
    /// be picked up by accident.
    /// </summary>
    public static RelayConfig Load(
        string? explicitPath,
        bool allowExampleFallback,
        string? workingDirectory = null,
        Func<string, string?>? getEnv = null)
    {
        var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
        getEnv ??= Environment.GetEnvironmentVariable;

        string path;
        string source;
        var (envName, env) = FirstEnv(getEnv, ProductInfo.LegacyConfigEnv, ProductInfo.ConfigEnv);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            path = Path.GetFullPath(explicitPath, cwd);
            source = "command line";
            if (!File.Exists(path))
                throw new ConfigException($"config not found: {path} (from the command line)");
        }
        else if (!string.IsNullOrWhiteSpace(env))
        {
            path = Path.GetFullPath(env, cwd);
            source = envName ?? ProductInfo.LegacyConfigEnv;
            if (!File.Exists(path))
                throw new ConfigException($"config not found: {path} (from {source})");
        }
        else if (File.Exists(Path.Combine(cwd, "config.json")))
        {
            path = Path.Combine(cwd, "config.json");
            source = "./config.json";
        }
        else if (allowExampleFallback && File.Exists(Path.Combine(cwd, "config.example.json")))
        {
            path = Path.Combine(cwd, "config.example.json");
            source = "./config.example.json";
            Console.Error.WriteLine("[chatbridge] warning: no config.json; running on config.example.json");
        }
        else
        {
            throw new ConfigException(allowExampleFallback
                ? "no config found: pass a path or --config, set MUSE_RELAY_CONFIG or CHATBRIDGE_CONFIG, or create ./config.json"
                : "no config found: pass --config <path>, set MUSE_RELAY_CONFIG or CHATBRIDGE_CONFIG, or run from the directory that holds config.json");
        }

        RelayConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<RelayConfig>(File.ReadAllText(path), JsonUtil.Opts)
                  ?? throw new ConfigException($"{path}: empty config");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path}: invalid JSON ({ex.Message})");
        }

        if (string.IsNullOrWhiteSpace(cfg.Url))
            cfg.Url = "wss://hack.chat/chat-ws";
        if (string.IsNullOrWhiteSpace(cfg.Origin))
            cfg.Origin = "https://hack.chat";
        if (string.IsNullOrWhiteSpace(cfg.Channel))
            throw new ConfigException($"{path}: channel is required");
        if (string.IsNullOrWhiteSpace(cfg.Nick))
            throw new ConfigException($"{path}: nick is required");

        cfg.AutoAck ??= new AutoAckConfig();
        cfg.AutoAck.Validate(path);
        cfg.Hook?.Validate(path);
        cfg.MentionRouting = cfg.Mentions ?? new MentionConfig();
        cfg.Roster = AgentDirectory.Parse(path, cfg.Agents, cfg.MentionRouting);
        if (!string.IsNullOrWhiteSpace(cfg.Trip) && PublicTrip.ForJoin(cfg.Trip) is null)
            throw new ConfigException($"{path}: trip must be a public code like Ab12Cd, not a password");

        var baseRaw = string.IsNullOrWhiteSpace(cfg.Base) ? "." : cfg.Base;
        var configDir = Path.GetDirectoryName(path) ?? cwd;
        cfg.BaseDir = Path.GetFullPath(baseRaw, configDir);
        cfg.ConfigPath = path;
        cfg.Source = source;
        cfg.ValidateReceiveIdle();
        return cfg;
    }

    private void ValidateReceiveIdle()
    {
        if (double.IsFinite(ReceiveIdleSeconds)
            && ReceiveIdleSeconds >= 0
            && ReceiveIdleSeconds <= MaxReceiveIdleSeconds)
            return;
        throw new ConfigException(
            $"{ConfigPath}: receive_idle_s must be from 0 to {MaxReceiveIdleSeconds:0} (0 disables the quiet-socket watchdog)");
    }

    private static (string? Name, string? Value) FirstEnv(Func<string, string?> getEnv, params string[] names)
    {
        foreach (var name in names)
        {
            var value = getEnv(name);
            if (!string.IsNullOrWhiteSpace(value))
                return (name, value);
        }

        return (null, null);
    }

    /// <summary>The hook poller's status file (<c>&lt;hook.state&gt;.status</c>), or null without a hook block.</summary>
    public string? HookStatusPath() => Hook is null ? null : HookStatus.PathFor(Hook.StatePath(BaseDir));

    /// <summary>What <c>status</c> and the auto-ack know about the hook poller right now.</summary>
    public HookView ReadHook(DateTimeOffset now) =>
        HookView.Classify(Hook is not null, HookStatusPath() is { } p ? HookStatus.TryRead(p) : null, now, ProcessInfo.IsRunning);
}
