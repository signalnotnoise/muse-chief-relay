using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBridge;

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
                "hook" => await CmdHookAsync(cli),
                "inbox" => CmdInbox(cli),
                "reconcile" => CmdReconcile(cli),
                "help" => CmdHelp(),
                _ => await RunAsync(cli)
            };
        }
        catch (Exception ex) when (ex is ConfigException or ArgumentException)
        {
            Console.Error.WriteLine($"[chatbridge] {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(CliArgs cli)
    {
        var cfg = RelayConfig.Load(cli.ConfigPath, allowExampleFallback: true);
        using var cts = new CancellationTokenSource();
        // SIGTERM / SIGINT: cancel and let RunForeverAsync finish, so the final state.json write
        // (alive=false) and the "stopped" line happen.
        using var signals = new ShutdownSignals(cts, sig => Console.WriteLine($"[chatbridge] {sig} received, shutting down…"));

        Console.WriteLine(
            $"[chatbridge] config={cfg.ConfigPath} ({cfg.Source}) channel={cfg.Channel} nick={cfg.Nick} base={cfg.BaseDir}");
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

        void Print(IReadOnlyList<WatchedChat> chats)
        {
            Console.Out.WriteLine(WatchedChat.ToJsonArray(chats));
            Console.Out.Flush();
        }

        if (!opts.Wait)
        {
            // A torn read here used to crash with a stack trace; report it as a clean error instead.
            if (!watcher.TryPoll(out var poll, out var error) || poll is null)
            {
                Console.Error.WriteLine($"[chatbridge] watch: cannot read the inbox ({error})");
                return 2;
            }
            if (watcher.Warning is { } w)
                Console.Error.WriteLine($"[chatbridge] warning: {w}");
            Print(poll.Chats); // printed before the offset is saved: a crash repeats, never loses
            watcher.Commit(poll);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        using var signals = new ShutdownSignals(cts, null);
        var outcome = await watcher.WaitAsync(opts.Timeout, Print, cts.Token);
        if (watcher.Warning is { } warn)
            Console.Error.WriteLine($"[chatbridge] warning: {warn}");

        switch (outcome)
        {
            case WaitOutcome.Delivered:
                return 0;
            case WaitOutcome.TimedOut:
                Print(Array.Empty<WatchedChat>());
                return WatchOptions.ExitTimeout;
            default:
                // Stopped by a signal: nothing new was delivered and the offset file is consistent.
                Print(Array.Empty<WatchedChat>());
                return 128 + (signals.Received == PosixSignal.SIGINT ? 2 : 15);
        }
    }

    private static async Task<int> CmdHookAsync(CliArgs cli)
    {
        var opts = HookOptions.Parse(cli.Rest);
        var cfg = RelayConfig.Load(cli.ConfigPath, allowExampleFallback: false);
        if (cfg.Hook is null)
            throw new ConfigException($"{cfg.ConfigPath}: no \"hook\" block; see README, \"Webhook poller\"");
        var secrets = HookSecrets.FromEnvironment(cfg.Hook);
        using var poller = new HookPoller(cfg, secrets);

        if (opts.Test)
        {
            // Wakes whatever is behind the webhook, with an obviously fake chat.
            var r = await poller.FireAsync(
                [new WatchedChat("Chief.Bridge", null, "(hook connectivity test, ignore)", JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))],
                CancellationToken.None);
            Console.WriteLine($"[chatbridge] hook test: {r.Result}");
            return r.Ok ? 0 : HookOptions.ExitTestFailed;
        }

        // The status file is a report, not a lock: two processes can both observe "not running"
        // and then both POST. The lock is held until this process exits, crash included.
        var statePath = cfg.Hook.StatePath(cfg.BaseDir);
        var gate = HookInstanceLock.TryAcquire(HookInstanceLock.PathFor(statePath));
        if (gate is null)
        {
            var other = cfg.ReadHook(DateTimeOffset.UtcNow).Status;
            var who = other is { } s && s.Pid != Environment.ProcessId ? $" (status last wrote pid {s.Pid})" : "";
            Console.Error.WriteLine($"[chatbridge] hook: another poller already holds {HookInstanceLock.PathFor(statePath)}{who}; not starting");
            return HookOptions.ExitAlreadyRunning;
        }

        using (gate)
        {
            using var cts = new CancellationTokenSource();
            using var signals = new ShutdownSignals(cts, sig => Console.WriteLine($"[chatbridge] hook: {sig} received, stopping…"));
            await poller.RunAsync(cts.Token);
        }

        return 0;
    }

    private static int CmdHelp()
    {
        Console.WriteLine(
            """
            ChatBridge — agent-agnostic room relay (desktop)

            Launch aliases that still run this program: Chief.Bridge, chief-bridge,
            dotnet run --project src/Chief.Bridge, and dotnet Chief.Bridge.dll.
            The new names are ChatBridge, chat-bridge, and dotnet run --project src/ChatBridge.
            Agent nicks come from config (agents[]). None are built into the program.

              [--config <path> | <path>]        Run the bridge until SIGTERM / Ctrl+C.
                                                Transient failures retry forever (backoff 1s–30s
                                                with jitter). A bad config exits 2; it is not retried.
              say [--config <path>] <text>      Append one chat line to {base}/outbox.jsonl and exit.
                                                Refuses CLI/shell probe text (exit 1, not queued).
              status [--config <path>] [--state <file>]
                                                Print channel/nick, the bridge state from state.json, the hook
                                                poller (running, last fire and its HTTP result), chats not yet
                                                drained by watch (offset --state, default
                                                {base}/.inbox_watch.offset), and whether auto-ack is on
              watch [--config <path>] [--nick <nick>] [--state <file>] [--wait [--timeout <s>]]
                                                Print new inbound chats from {base}/inbox.jsonl as a JSON array
                                                of {nick,trip,text,ts}, skipping the bridge's own nick. The offset
                                                is kept in --state (default {base}/.inbox_watch.offset); the first
                                                run only records it and prints []. --wait blocks until at least one
                                                new chat arrives. Exit codes: 0 printed, 2 usage/config error (also an
                                                unreadable inbox), 3 --timeout reached (prints []), 130/143 stopped
                                                by SIGINT/SIGTERM (prints []).
              hook [--config <path>] [--test]    Poll {base}/inbox.jsonl and POST new inbound chats to a webhook
                                                (config "hook" block; URL and key from the environment variables it
                                                names). Runs until SIGTERM / Ctrl+C. Batches by cooldown, retries
                                                failures, keeps its own offset and a status file for `status`.
                                                --test sends one fake chat and prints the HTTP result. Exit codes:
                                                0 stopped cleanly or test OK, 2 usage/config error, 4 another hook
                                                poller is already running, 5 test failed.
              inbox due --agent <id>           Print due room wake events for one agent (JSON array, seq order).
              inbox pending --agent <id>       Print every unacked room event, including those in backoff.
              inbox ack --agent <id> <event>   Acknowledge one event. Idempotent. Exit 1 if the id is unknown.
              inbox fail --agent <id> <event>  Record a soft adapter failure and its backoff. Exit 1 if unknown.
              reconcile --id <client_msg_id> requeue|drop
                                                Operator path for one uncertain v2 send. Does not connect
                                                and does not turn protocol_v2 on. drop does not send.
                                                requeue may duplicate on the server. A sent row is never
                                                resent until this command (or a later accepted). Exit 1
                                                when that id is not an uncertain send.
              help                              This text

            The inbox commands are the adapter contract (docs/chatbridge.md). They do not call a model.
            Config for the bridge run: --config or the first argument, then MUSE_RELAY_CONFIG, then
            CHATBRIDGE_CONFIG, then ./config.json, then ./config.example.json (with a warning).
            Config for say/status/watch/hook/inbox/reconcile: --config, then MUSE_RELAY_CONFIG, then
            CHATBRIDGE_CONFIG, then ./config.json. No other fallback.
            An explicit path that doesn't exist is an error; it never falls through to another file.
            Use -- to end options, e.g. say -- --config is literal text.
            """);
        return 0;
    }

    private static int CmdInbox(CliArgs cli)
    {
        string? agent = null;
        string? eventId = null;
        string? command = null;
        for (var i = 0; i < cli.Rest.Count; i++)
        {
            var a = cli.Rest[i];
            if (a == "--agent" && i + 1 < cli.Rest.Count && !string.IsNullOrWhiteSpace(cli.Rest[i + 1]))
                agent = cli.Rest[++i];
            else if (a.StartsWith("--agent=", StringComparison.Ordinal) && a.Length > "--agent=".Length)
                agent = a["--agent=".Length..];
            else if (command is null && a is "due" or "pending" or "ack" or "fail")
                command = a;
            else if (eventId is null && command is "ack" or "fail")
                eventId = a;
            else
                throw new ArgumentException($"inbox: unknown argument '{a}'");
        }

        if (command is null || string.IsNullOrWhiteSpace(agent))
            throw new ArgumentException("usage: ChatBridge inbox due|pending|ack|fail --agent <id> [event-id]");
        if (!AgentPath.IsSafeId(agent))
            throw new ArgumentException("inbox: agent id must be a single path segment");
        if (command is "ack" or "fail" && string.IsNullOrWhiteSpace(eventId))
            throw new ArgumentException($"usage: ChatBridge inbox {command} --agent <id> <event-id>");

        var cfg = RelayConfig.Load(cli.ConfigPath, allowExampleFallback: false);
        var agentsRoot = Path.Combine(cfg.BaseDir, "agents");
        var inbox = AgentInbox.Open(Path.Combine(agentsRoot, agent));
        inbox.OnFiled = new ConversationStore(agentsRoot).AppendRoom;
        var now = DateTimeOffset.UtcNow;
        switch (command)
        {
            case "due":
                PrintWakes(inbox.Due(now));
                return 0;
            case "pending":
                PrintWakes(inbox.Pending());
                return 0;
            case "ack":
                if (!inbox.Ack(eventId!, now))
                {
                    Console.Error.WriteLine($"{ProductInfo.Prefix("inbox")} unknown event {eventId}");
                    return 1;
                }

                Console.WriteLine($"acked {eventId}");
                return 0;
            default:
                if (!inbox.RecordFailure(eventId!, "adapter", now))
                {
                    Console.Error.WriteLine($"{ProductInfo.Prefix("inbox")} unknown event {eventId}");
                    return 1;
                }

                var pending = inbox.Pending().FirstOrDefault(v => v.Event.Id == eventId);
                Console.WriteLine($"retry {eventId} next_unix {pending?.NextUnix}");
                return 0;
        }
    }

    private static void PrintWakes(IReadOnlyList<InboxView> views)
    {
        var requests = views.Select(InboxWakeRequest.Create).ToList();
        Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(requests, JsonUtil.Opts));
        Console.Out.Flush();
    }

    private static async Task<int> CmdSayAsync(CliArgs cli)
    {
        if (cli.Rest.Count == 0)
        {
            Console.Error.WriteLine($"usage: {ProductInfo.Name} say [--config <path>] <text>");
            return 1;
        }

        var text = string.Join(' ', cli.Rest);
        if (CliProbeText.IsFragment(text))
        {
            Console.Error.WriteLine($"{ProductInfo.Name} say: refused CLI/shell probe text (not queued)");
            return 1;
        }

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
        var now = DateTimeOffset.UtcNow;
        var hook = cfg.ReadHook(now);
        Console.WriteLine($"hook: {hook.Detail}");
        if (HookView.LastFireLine(hook.Status, now) is { } last)
            Console.WriteLine(last);
        PrintUndrained(cfg, Path.GetFullPath(watchState ?? Path.Combine(cfg.BaseDir, ".inbox_watch.offset")));
        var ack = cfg.AutoAck;
        Console.WriteLine(ack.Enabled
            ? $"auto-ack: on (mentions+tasks from {ack.MentionTrips.Count} trip(s), tasks only from {ack.TaskTrips.Count}, cooldown {ack.CooldownSeconds:0.#}s, max {ack.MaxPerHour}/h)"
            : "auto-ack: off");
        Console.WriteLine(cfg.MentionRouting.Enabled
            ? $"mentions: on ({cfg.Roster.Agents.Count} agent(s), max fan-out hop {cfg.MentionRouting.MaxFanoutHop})"
            : "mentions: off");
        PrintV2(cfg);
        return 0;
    }

    /// <summary>
    /// Report uncertain v2 sends. Does not connect and does not create the ledger.
    /// Ids only: the held text is not printed.
    /// </summary>
    private static void PrintV2(RelayConfig cfg)
    {
        var outbound = Path.Combine(cfg.BaseDir, V2Client.OutboundName);
        if (!File.Exists(outbound))
        {
            Console.WriteLine(cfg.ProtocolV2 ? "v2: on, no uncertain send" : "v2: off");
            return;
        }

        var ids = V2Client.Open(cfg.BaseDir).UncertainIds();
        if (ids.Count == 0)
        {
            Console.WriteLine(cfg.ProtocolV2 ? "v2: on, no uncertain send" : "v2: ledger present, no uncertain send");
            return;
        }

        Console.WriteLine(
            $"v2: {ids.Count} uncertain send(s); not resent automatically. reconcile --id <client_msg_id> drop|requeue (requeue may duplicate)");
        foreach (var id in ids)
            Console.WriteLine("v2 uncertain: " + id);
    }

    private static int CmdReconcile(CliArgs cli)
    {
        string? id = null;
        string? decision = null;
        for (var i = 0; i < cli.Rest.Count; i++)
        {
            var a = cli.Rest[i];
            if (a == "--id" && i + 1 < cli.Rest.Count && !string.IsNullOrWhiteSpace(cli.Rest[i + 1]))
                id = cli.Rest[++i];
            else if (a.StartsWith("--id=", StringComparison.Ordinal) && a.Length > "--id=".Length)
                id = a["--id=".Length..];
            else if (decision is null && a is "requeue" or "drop")
                decision = a;
            else
                throw new ArgumentException($"reconcile: unknown argument '{a}'");
        }

        if (string.IsNullOrWhiteSpace(id) || decision is null)
            throw new ArgumentException("usage: ChatBridge reconcile --id <client_msg_id> requeue|drop");

        var cfg = RelayConfig.Load(cli.ConfigPath, allowExampleFallback: false);
        if (!V2Client.Open(cfg.BaseDir).ResolveUncertain(id, decision))
        {
            Console.Error.WriteLine($"not reconciled: {id} is not an uncertain send");
            return 1;
        }

        Console.WriteLine(decision == "requeue"
            ? $"reconciled {id} requeue (may duplicate on the server)"
            : $"reconciled {id} drop");
        return 0;
    }

    /// <summary>What the next <c>watch</c> would return: chats past the saved offset. Read-only.</summary>
    private static void PrintUndrained(RelayConfig cfg, string offsetPath)
    {
        if (!File.Exists(offsetPath))
        {
            Console.WriteLine($"undrained: unknown (no watch offset file {offsetPath} yet)");
            return;
        }

        try
        {
            var poll = new InboxWatcher(Path.Combine(cfg.BaseDir, "inbox.jsonl"), offsetPath, cfg.Nick).Poll();
            if (poll.Chats.Count == 0)
            {
                Console.WriteLine("undrained: 0 chats");
                return;
            }

            var first = poll.Chats[0];
            var when = first.Ts is JsonValue v && v.TryGetValue<long>(out var ts)
                ? $" at {DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime():HH:mm:ss}"
                : "";
            Console.WriteLine($"undrained: {poll.Chats.Count} chat(s) not yet read by watch (oldest from {first.Nick}{when})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"undrained: (unreadable: {ex.GetType().Name})");
        }
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
            if (root.TryGetProperty("reason", out var reasonEl)
                && reasonEl.ValueKind == JsonValueKind.String
                && reasonEl.GetString() is { Length: > 0 } reason)
                Console.WriteLine($"reason: {reason}");
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
