using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chief.Bridge;

internal static class JsonUtil
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        // Keep ' and non-ASCII text readable in the .jsonl files and on the wire. The output is still
        // valid JSON; it is never embedded in HTML or a <script> block.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var cli = CliArgs.Parse(args);
            return cli.Command switch
            {
                "say" => await CmdSayAsync(cli),
                "status" => CmdStatus(cli),
                "watch" => await CmdWatchAsync(cli),
                "help" => CmdHelp(),
                _ => await RunAsync(cli)
            };
        }
        catch (Exception ex) when (ex is ConfigException or ArgumentException)
        {
            Console.Error.WriteLine($"[chief] {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(CliArgs cli)
    {
        var cfg = RelayConfig.Load(cli.ConfigPath, allowExampleFallback: true);
        using var cts = new CancellationTokenSource();
        // SIGTERM / SIGINT: cancel and let RunForeverAsync finish, so the final state.json write
        // (alive=false) and the "stopped" line happen.
        using var signals = new ShutdownSignals(cts, sig => Console.WriteLine($"[chief] {sig} received, shutting down…"));

        Console.WriteLine(
            $"[chief] config={cfg.ConfigPath} ({cfg.Source}) channel={cfg.Channel} nick={cfg.Nick} base={cfg.BaseDir}");
        var bridge = new HackChatBridge(cfg);
        await bridge.RunForeverAsync(cts.Token);
        return 0;
    }

    private static async Task<int> CmdWatchAsync(CliArgs cli)
    {
        var opts = WatchOptions.Parse(cli.Rest);
        var cfg = RelayConfig.Load(cli.ConfigPath, allowExampleFallback: false);
        var nick = opts.Nick ?? cfg.Nick;
        var statePath = Path.GetFullPath(opts.StatePath ?? Path.Combine(cfg.BaseDir, ".inbox_watch.offset"));
        var watcher = new InboxWatcher(Path.Combine(cfg.BaseDir, "inbox.jsonl"), statePath, nick);
        // <offset file>.status: armed / heartbeat / how the last run ended, for `status` and the auto-ack.
        var status = new WatchStatusWriter(WatchStatus.PathFor(statePath));
        var delivered = 0;

        void Print(IReadOnlyList<WatchedChat> chats)
        {
            Console.Out.WriteLine(WatchedChat.ToJsonArray(chats));
            Console.Out.Flush();
            delivered = chats.Count;
        }

        if (!opts.Wait)
        {
            var poll = watcher.Poll();
            if (watcher.Warning is { } w)
                Console.Error.WriteLine($"[chief] warning: {w}");
            Print(poll.Chats); // printed before the offset is saved: a crash repeats, never loses
            watcher.Commit(poll);
            status.Polled(poll.Chats.Count);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        using var signals = new ShutdownSignals(cts, null);
        if (WatchStatus.TryRead(status.Path) is { State: WatchStatus.Armed or WatchStatus.Settling } other
            && other.Pid != Environment.ProcessId && ProcessInfo.IsRunning(other.Pid))
        {
            // Two watchers on one offset file both deliver the same chats, so the agent answers twice.
            Console.Error.WriteLine(
                $"[chief] warning: another watcher (pid {other.Pid}) is already armed on {statePath}; run one watcher per offset file");
        }

        status.Armed(opts.Timeout, opts.Settle?.TotalSeconds ?? 0);
        var outcome = await watcher.WaitAsync(opts.Timeout, Print, cts.Token, opts.Settle, status.Tick);
        if (watcher.Warning is { } warn)
            Console.Error.WriteLine($"[chief] warning: {warn}");

        switch (outcome)
        {
            case WaitOutcome.Delivered:
                status.Exited(WatchStatus.DeliveredState, 0, delivered);
                return 0;
            case WaitOutcome.TimedOut:
                Print(Array.Empty<WatchedChat>());
                status.Exited(WatchStatus.TimedOut, WatchOptions.ExitTimeout, 0);
                // Exit 3 is a wake like any other. Not re-arming after a quiet timeout is how a listener goes
                // missing without anyone noticing.
                Console.Error.WriteLine("[chief] watch timed out with nothing new: re-arm now (watch --wait)");
                return WatchOptions.ExitTimeout;
            default:
                // Stopped by a signal: nothing new was delivered and the offset file is consistent.
                Print(Array.Empty<WatchedChat>());
                var code = 128 + (signals.Received == PosixSignal.SIGINT ? 2 : 15);
                status.Exited(WatchStatus.Stopped, code, 0);
                return code;
        }
    }

    private static int CmdHelp()
    {
        Console.WriteLine(
            """
            Chief.Bridge — Muse↔Chief hack.chat WSS relay (desktop)

              [--config <path> | <path>]        Run the bridge until SIGTERM / Ctrl+C.
                                                Transient failures retry forever (backoff 1s–30s
                                                with jitter). A bad config exits 2; it is not retried.
              say [--config <path>] <text>      Append one chat line to {base}/outbox.jsonl and exit
              status [--config <path>] [--state <file>]
                                                Print channel/nick, the bridge state from state.json, whether a
                                                watch listener is armed (from <state>.status; default
                                                {base}/.inbox_watch.offset), chats waiting for it, and auto-ack
              watch [--config <path>] [--nick <nick>] [--state <file>] [--wait [--timeout <s>] [--settle <s>]]
                                                Print new inbound chats from {base}/inbox.jsonl as a JSON array
                                                of {nick,trip,text,ts}, skipping the bridge's own nick. The offset
                                                is kept in --state (default {base}/.inbox_watch.offset); the first
                                                run only records it and prints []. --wait blocks until at least one
                                                new chat arrives; --settle keeps collecting until the burst has been
                                                quiet for <s> seconds (0-60, at most 4x<s> in all) and returns it as
                                                one array. Writes <state>.status (armed, heartbeat, how it ended) for
                                                `status`. Exit codes: 0 printed, 2 usage/config error, 3 --timeout
                                                reached (prints []), 130/143 stopped by SIGINT/SIGTERM (prints []).
              help                              This text

            Config for the bridge run: --config or the first argument, then MUSE_RELAY_CONFIG, then
            ./config.json, then ./config.example.json (with a warning).
            Config for say/status/watch: --config, then MUSE_RELAY_CONFIG, then ./config.json. No other fallback.
            An explicit path that doesn't exist is an error; it never falls through to another file.
            Use -- to end options, e.g. say -- --config is literal text.
            """);
        return 0;
    }

    private static async Task<int> CmdSayAsync(CliArgs cli)
    {
        if (cli.Rest.Count == 0)
        {
            Console.Error.WriteLine("usage: Chief.Bridge say [--config <path>] <text>");
            return 1;
        }

        var text = string.Join(' ', cli.Rest);
        var cfg = RelayConfig.Load(cli.ConfigPath, allowExampleFallback: false);
        Directory.CreateDirectory(cfg.BaseDir);
        var outbox = Path.Combine(cfg.BaseDir, "outbox.jsonl");
        var line = JsonSerializer.Serialize(new { cmd = "chat", text }, JsonUtil.Opts) + "\n";
        // One append call per line, newline included, so the bridge never sees a half-written line
        // as complete (it only consumes newline-terminated lines anyway).
        await File.AppendAllTextAsync(outbox, line);
        Console.WriteLine($"queued -> {outbox}");
        return 0;
    }

    private static int CmdStatus(CliArgs cli)
    {
        string? watchState = null;
        for (var i = 0; i < cli.Rest.Count; i++)
        {
            var a = cli.Rest[i];
            if (a == "--state" && i + 1 < cli.Rest.Count && !string.IsNullOrWhiteSpace(cli.Rest[i + 1]))
                watchState = cli.Rest[++i];
            else if (a.StartsWith("--state=", StringComparison.Ordinal) && a.Length > "--state=".Length)
                watchState = a["--state=".Length..];
            else
                throw new ArgumentException($"status: unknown argument '{a}'");
        }

        var cfg = RelayConfig.Load(cli.ConfigPath, allowExampleFallback: false);
        Console.WriteLine($"config: {cfg.ConfigPath} ({cfg.Source})");
        Console.WriteLine($"channel: {cfg.Channel}");
        Console.WriteLine($"nick: {cfg.Nick}");
        var statePath = Path.Combine(cfg.BaseDir, "state.json");
        Console.WriteLine($"state: {statePath}");
        PrintBridgeState(statePath);
        PrintListener(cfg, Path.GetFullPath(watchState ?? Path.Combine(cfg.BaseDir, ".inbox_watch.offset")));
        PrintAutoAck(cfg.AutoAck);
        return 0;
    }

    private static void PrintListener(RelayConfig cfg, string offsetPath)
    {
        var view = ListenerView.Classify(WatchStatus.TryRead(WatchStatus.PathFor(offsetPath)), DateTimeOffset.UtcNow, ProcessInfo.IsRunning);
        Console.WriteLine($"listener: {view.Detail}");

        // What is sitting in the inbox past the saved offset, i.e. what the next watch would return. Read-only:
        // nothing is committed, and without an offset file there is nothing to compare against.
        if (!File.Exists(offsetPath))
        {
            Console.WriteLine("waiting: unknown (no offset file yet)");
            return;
        }

        try
        {
            var poll = new InboxWatcher(Path.Combine(cfg.BaseDir, "inbox.jsonl"), offsetPath, cfg.Nick).Poll();
            if (poll.Chats.Count == 0)
            {
                Console.WriteLine("waiting: 0 chats");
            }
            else
            {
                var first = poll.Chats[0];
                var when = first.Ts is JsonValue v && v.TryGetValue<long>(out var ts)
                    ? $" at {DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime():HH:mm:ss}"
                    : "";
                Console.WriteLine($"waiting: {poll.Chats.Count} chat(s) not yet delivered to a watcher (oldest from {first.Nick}{when})");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"waiting: (unreadable: {ex.Message})");
        }
    }

    private static void PrintAutoAck(AutoAckConfig a)
    {
        Console.WriteLine(a.Enabled
            ? $"auto-ack: on (mentions+tasks from {a.MentionTrips.Count} trip(s), tasks only from {a.TaskTrips.Count}, cooldown {a.CooldownSeconds:0.#}s, max {a.MaxPerHour}/h)"
            : "auto-ack: off");
    }

    private static void PrintBridgeState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            Console.WriteLine("alive: false (state.json missing)");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = doc.RootElement;
            bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            Console.WriteLine($"alive: {(Flag("alive") ? "true" : "false")}");
            Console.WriteLine($"connected: {(Flag("connected") ? "true" : "false")}");
            Console.WriteLine($"reconnecting: {(Flag("reconnecting") ? "true" : "false")}");
            if (root.TryGetProperty("at", out var at) && at.TryGetInt64(out var atSecs))
                Console.WriteLine($"at: {atSecs} ({DateTimeOffset.FromUnixTimeSeconds(atSecs).ToLocalTime():yyyy-MM-dd HH:mm:ss zzz})");
            if (root.TryGetProperty("pid", out var pidEl) && pidEl.TryGetInt32(out var pid))
            {
                var running = ProcessInfo.IsRunning(pid);
                Console.WriteLine(running ? $"pid: {pid} (running)"
                    : Flag("alive") ? $"pid: {pid} (not running: the bridge died without a clean stop; this state is stale)"
                    : $"pid: {pid} (not running)");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($"state: (unreadable: {ex.Message})");
        }
    }
}
