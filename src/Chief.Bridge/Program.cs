using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chief.Bridge;

internal static class JsonUtil
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "say" or "status" or "help" or "-h" or "--help")
        {
            return args[0] switch
            {
                "say" => await CmdSayAsync(args),
                "status" => CmdStatus(),
                _ => CmdHelp()
            };
        }

        // Optional config path as first arg (not a subcommand)
        string? configArg = args.Length > 0 ? args[0] : null;
        var cfg = RelayConfig.Load(configArg);
        var bridge = new HackChatBridge(cfg);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"[chief] channel={cfg.Channel} nick={cfg.Nick} base={cfg.BaseDir}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        await bridge.RunForeverAsync(cts.Token);
        return 0;
    }

    private static int CmdHelp()
    {
        Console.WriteLine(
            """
            Chief.Bridge — Muse↔Chief hack.chat WSS relay (desktop)

              (default)           Run the bridge forever
              say <text>          Append one chat line to outbox and exit
              status              Print channel/nick/alive from state.json
              <config-path>       Optional config.json path as first arg

            Config: MUSE_RELAY_CONFIG env, or config.json next to the app /
            cwd, or config.example.json. Schema: url, origin, channel, nick, base.
            """);
        return 0;
    }

    private static async Task<int> CmdSayAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Chief.Bridge say <text>");
            return 1;
        }

        var text = string.Join(' ', args.Skip(1));
        var cfg = RelayConfig.Load(null);
        Directory.CreateDirectory(cfg.BaseDir);
        var outbox = Path.Combine(cfg.BaseDir, "outbox.jsonl");
        var line = JsonSerializer.Serialize(new { cmd = "chat", text }, JsonUtil.Opts) + "\n";
        await File.AppendAllTextAsync(outbox, line);
        Console.WriteLine("queued");
        return 0;
    }

    private static int CmdStatus()
    {
        var cfg = RelayConfig.Load(null);
        Console.WriteLine($"channel: {cfg.Channel}");
        Console.WriteLine($"nick: {cfg.Nick}");
        var statePath = Path.Combine(cfg.BaseDir, "state.json");
        if (!File.Exists(statePath))
        {
            Console.WriteLine("alive: false");
            Console.WriteLine("state: (missing)");
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = doc.RootElement;
            var alive = root.TryGetProperty("alive", out var a) && a.ValueKind == JsonValueKind.True;
            var connected = root.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True;
            Console.WriteLine($"alive: {alive}");
            Console.WriteLine($"connected: {connected}");
            if (root.TryGetProperty("at", out var at))
                Console.WriteLine($"at: {at}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"state: (unreadable: {ex.Message})");
        }

        return 0;
    }
}

internal sealed class RelayConfig
{
    public string Url { get; set; } = "wss://hack.chat/chat-ws";
    public string Origin { get; set; } = "https://hack.chat";
    public string Channel { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Base { get; set; } = ".";

    [System.Text.Json.Serialization.JsonIgnore]
    public string BaseDir { get; set; } = ".";

    [System.Text.Json.Serialization.JsonIgnore]
    public string ConfigPath { get; set; } = "";

    public static RelayConfig Load(string? cliPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(cliPath)
            && !cliPath.Equals("say", StringComparison.OrdinalIgnoreCase)
            && !cliPath.Equals("status", StringComparison.OrdinalIgnoreCase)
            && !cliPath.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(cliPath);
        }

        var env = Environment.GetEnvironmentVariable("MUSE_RELAY_CONFIG");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);

        var appDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "config.json"));
        candidates.Add(Path.Combine(appDir, "config.json"));

        var walk = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 5 && walk != null; i++, walk = walk.Parent)
        {
            candidates.Add(Path.Combine(walk.FullName, "config.json"));
            candidates.Add(Path.Combine(walk.FullName, "config.example.json"));
        }

        candidates.Add(Path.Combine(appDir, "config.example.json"));

        string? found = null;
        foreach (var c in candidates.Distinct())
        {
            if (File.Exists(c))
            {
                found = c;
                break;
            }
        }

        if (found is null)
        {
            throw new FileNotFoundException(
                "No config.json found. Copy config.example.json to config.json or set MUSE_RELAY_CONFIG.");
        }

        var json = File.ReadAllText(found);
        var cfg = JsonSerializer.Deserialize<RelayConfig>(json, JsonUtil.Opts)
                  ?? throw new InvalidOperationException($"Failed to parse {found}");

        if (string.IsNullOrWhiteSpace(cfg.Url))
            cfg.Url = "wss://hack.chat/chat-ws";
        if (string.IsNullOrWhiteSpace(cfg.Origin))
            cfg.Origin = "https://hack.chat";
        if (string.IsNullOrWhiteSpace(cfg.Channel))
            throw new InvalidOperationException("config.channel is required");
        if (string.IsNullOrWhiteSpace(cfg.Nick))
            throw new InvalidOperationException("config.nick is required");

        var baseRaw = string.IsNullOrWhiteSpace(cfg.Base) ? "." : cfg.Base;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(found)) ?? Directory.GetCurrentDirectory();
        cfg.BaseDir = Path.IsPathRooted(baseRaw)
            ? Path.GetFullPath(baseRaw)
            : Path.GetFullPath(Path.Combine(configDir, baseRaw));

        Directory.CreateDirectory(cfg.BaseDir);
        cfg.ConfigPath = found;
        return cfg;
    }
}

internal sealed class HackChatBridge
{
    private readonly RelayConfig _cfg;
    private readonly string _inbox;
    private readonly string _outbox;
    private readonly string _unread;
    private readonly string _state;
    private long _outPos;
    private readonly object _fileLock = new();

    public HackChatBridge(RelayConfig cfg)
    {
        _cfg = cfg;
        _inbox = Path.Combine(cfg.BaseDir, "inbox.jsonl");
        _outbox = Path.Combine(cfg.BaseDir, "outbox.jsonl");
        _unread = Path.Combine(cfg.BaseDir, "unread.jsonl");
        _state = Path.Combine(cfg.BaseDir, "state.json");
        _outPos = File.Exists(_outbox) ? new FileInfo(_outbox).Length : 0;
    }

    public async Task RunForeverAsync(CancellationToken ct)
    {
        var backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
                backoff = 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogEvent("err", new { error = ex.Message });
                Console.Error.WriteLine($"[chief] disconnect: {ex.Message}");
            }

            WriteState(alive: false, connected: false, reconnecting: true);
            if (ct.IsCancellationRequested) break;

            Console.WriteLine($"[chief] reconnect in {backoff}s…");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = Math.Min(backoff * 2, 30);
        }

        WriteState(alive: false, connected: false, reconnecting: false);
        Console.WriteLine("[chief] stopped");
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", _cfg.Origin);

        Console.WriteLine($"[chief] connecting {_cfg.Url}…");
        await ws.ConnectAsync(new Uri(_cfg.Url), ct);

        var join = new { cmd = "join", channel = _cfg.Channel, nick = _cfg.Nick };
        await SendJsonAsync(ws, join, ct);
        LogEvent("out", join);
        WriteState(alive: true, connected: true, reconnecting: false);
        Console.WriteLine($"[chief] joined #{_cfg.Channel} as {_cfg.Nick}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvTask = ReceiveLoopAsync(ws, linked.Token);
        var outTask = OutboxLoopAsync(ws, linked.Token);

        await Task.WhenAny(recvTask, outTask);
        linked.Cancel();
        try { await Task.WhenAll(recvTask, outTask); }
        catch { /* drain */ }

        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* ignore */ }
        }

        throw new IOException("WebSocket session ended");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            var raw = sb.ToString();
            JsonNode? node = null;
            try { node = JsonNode.Parse(raw); }
            catch { /* fall through */ }

            object msg = node ?? (object)new { raw };
            LogEvent("in", msg);

            if (node is not null)
            {
                var cmd = node["cmd"]?.GetValue<string>();
                if (cmd == "chat")
                {
                    var nick = node["nick"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(nick)
                        && !string.Equals(nick, _cfg.Nick, StringComparison.Ordinal))
                    {
                        var text = node["text"]?.GetValue<string>() ?? "";
                        var channel = node["channel"]?.GetValue<string>() ?? _cfg.Channel;
                        AppendUnread(new
                        {
                            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            nick,
                            text,
                            channel
                        });
                    }
                }
            }

            WriteState(alive: true, connected: true, reconnecting: false);
        }
    }

    private async Task OutboxLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                await DrainOutboxAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ws.State == WebSocketState.Open)
            {
                LogEvent("err", new { error = $"outbox: {ex.Message}" });
            }

            try { await Task.Delay(350, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DrainOutboxAsync(ClientWebSocket ws, CancellationToken ct)
    {
        if (!File.Exists(_outbox))
            return;

        long size;
        lock (_fileLock)
            size = new FileInfo(_outbox).Length;

        if (size < _outPos)
            _outPos = 0;
        if (size == _outPos)
            return;

        string chunk;
        lock (_fileLock)
        {
            using var fs = new FileStream(_outbox, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_outPos, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            chunk = reader.ReadToEnd();
            _outPos = fs.Position;
        }

        foreach (var line in chunk.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            object payload;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("cmd", out _))
                {
                    payload = JsonNode.Parse(trimmed)!;
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object
                         && doc.RootElement.TryGetProperty("text", out var t))
                {
                    payload = new { cmd = "chat", text = t.GetString() ?? trimmed };
                }
                else
                {
                    payload = new { cmd = "chat", text = trimmed };
                }
            }
            catch
            {
                payload = new { cmd = "chat", text = trimmed };
            }

            await SendJsonAsync(ws, payload, ct);
            LogEvent("out", payload);
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = payload is JsonNode node
            ? node.ToJsonString(JsonUtil.Opts)
            : JsonSerializer.Serialize(payload, JsonUtil.Opts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private void LogEvent(string dir, object msg)
    {
        JsonNode msgNode = msg switch
        {
            JsonNode jn => jn.DeepClone(),
            _ => JsonSerializer.SerializeToNode(msg, JsonUtil.Opts) ?? new JsonObject()
        };

        var row = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["dir"] = dir,
            ["msg"] = msgNode
        };
        AppendJsonl(_inbox, row.ToJsonString(JsonUtil.Opts));
    }

    private void AppendUnread(object row)
    {
        var line = JsonSerializer.Serialize(row, JsonUtil.Opts);
        AppendJsonl(_unread, line);
    }

    private void AppendJsonl(string path, string line)
    {
        lock (_fileLock)
        {
            File.AppendAllText(path, line + "\n");
        }
    }

    private void WriteState(bool alive, bool connected, bool reconnecting)
    {
        var obj = new
        {
            alive,
            at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            channel = _cfg.Channel,
            nick = _cfg.Nick,
            connected,
            reconnecting
        };
        var json = JsonSerializer.Serialize(obj, JsonUtil.Opts);
        lock (_fileLock)
        {
            File.WriteAllText(_state, json);
        }
    }
}
