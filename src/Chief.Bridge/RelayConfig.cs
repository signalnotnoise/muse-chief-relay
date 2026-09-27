using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chief.Bridge;

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

    // Optional instant acknowledgement from the bridge when a trusted trip addresses it. Off by default.
    [JsonPropertyName("auto_ack")] public AutoAckConfig AutoAck { get; set; } = new();

    // Optional webhook poller settings for `Chief.Bridge hook`. Null: not configured.
    public HookConfig? Hook { get; set; }

    [JsonIgnore] public string BaseDir { get; set; } = ".";
    [JsonIgnore] public string ConfigPath { get; set; } = "";
    [JsonIgnore] public string Source { get; set; } = "";

    /// <summary>
    /// Resolve and load the config.
    /// <list type="number">
    /// <item><paramref name="explicitPath"/> (--config or the bridge's first argument)</item>
    /// <item>MUSE_RELAY_CONFIG</item>
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
        var env = getEnv("MUSE_RELAY_CONFIG");
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
            source = "MUSE_RELAY_CONFIG";
            if (!File.Exists(path))
                throw new ConfigException($"config not found: {path} (from MUSE_RELAY_CONFIG)");
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
            Console.Error.WriteLine("[chief] warning: no config.json; running on config.example.json");
        }
        else
        {
            throw new ConfigException(allowExampleFallback
                ? "no config found: pass a path or --config, set MUSE_RELAY_CONFIG, or create ./config.json"
                : "no config found: pass --config <path>, set MUSE_RELAY_CONFIG, or run from the directory that holds config.json");
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

        var baseRaw = string.IsNullOrWhiteSpace(cfg.Base) ? "." : cfg.Base;
        var configDir = Path.GetDirectoryName(path) ?? cwd;
        cfg.BaseDir = Path.GetFullPath(baseRaw, configDir);
        cfg.ConfigPath = path;
        cfg.Source = source;
        return cfg;
    }

    /// <summary>The hook poller's status file (<c>&lt;hook.state&gt;.status</c>), or null without a hook block.</summary>
    public string? HookStatusPath() => Hook is null ? null : HookStatus.PathFor(Hook.StatePath(BaseDir));

    /// <summary>What <c>status</c> and the auto-ack know about the hook poller right now.</summary>
    public HookView ReadHook(DateTimeOffset now) =>
        HookView.Classify(Hook is not null, HookStatusPath() is { } p ? HookStatus.TryRead(p) : null, now, ProcessInfo.IsRunning);
}
