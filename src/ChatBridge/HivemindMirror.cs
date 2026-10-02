using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatBridge;

/// <summary>
/// Hands an accepted room chat to the Node HIVEMIND mirror. Off unless
/// <c>HIVEMIND_MESSAGE_MIRROR</c> is <c>1</c>. The chat path only enqueues;
/// Appwrite work runs on a background thread in the Node helper. A failure
/// here is a log line. It does not fail the bridge.
/// </summary>
internal interface IHivemindMirror
{
    void Offer(JsonObject chat, string channelFallback);
}

internal static class HivemindMirror
{
    public static IHivemindMirror? Open(BridgeRuntime runtime, Action<string> log)
    {
        var env = runtime.Env ?? Environment.GetEnvironmentVariable;
        var flag = env("HIVEMIND_MESSAGE_MIRROR");
        if (!string.Equals(flag?.Trim(), "1", StringComparison.Ordinal))
            return null;
        if (runtime.MirrorOffer is { } offer)
            return new DelegateHivemindMirror(offer, log);
        return NodeHivemindMirror.Start(env, log);
    }
}

internal static class MirrorPayload
{
    private static readonly Regex Uuid = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Seconds ceiling for a stored HIVEMIND timestamp (year 5138).
    /// A voizle chat <c>ts</c> is Unix milliseconds from <c>Date.now()</c>
    /// and is larger than this. Those values are not stored raw.
    /// </summary>
    public const long TsMax = 100_000_000_000L;

    /// <summary>Inclusive ceiling for a Unix-millisecond stamp, equal to <see cref="TsMax"/> seconds.</summary>
    public const long TsMaxMillis = TsMax * 1000L;

    public readonly record struct Result(bool Ok, string Json, string Reason);

    /// <summary>Lowercase hex SHA-256 of the trimmed channel. Same bytes as the Node board key.</summary>
    public static string WorkspaceKey(string? channel)
    {
        var trimmed = (channel ?? "").Trim();
        if (trimmed.Length == 0)
            return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant();
    }

    /// <summary>
    /// JSON for the Node helper. The channel is hashed into <c>workspaceKey</c> and dropped.
    /// Trip, password, pass, and token are not copied.
    /// </summary>
    public static Result Build(JsonObject chat, string? channelFallback, long nowSeconds)
    {
        var id = Json.Str(chat, "id");
        if (id is null || !Uuid.IsMatch(id))
            return new Result(false, "", "id");
        var key = WorkspaceKey(Json.Str(chat, "channel") ?? Json.Str(chat, "room") ?? channelFallback);
        if (key.Length != 64)
            return new Result(false, "", "key");
        var sender = (Json.Str(chat, "nick") ?? "").Trim();
        if (sender.Length is 0 or > 64)
            return new Result(false, "", "sender");
        if (Json.Str(chat, "text") is not { } text || text.Length == 0)
            return new Result(false, "", "missing");
        if (text.Length > 8192)
            return new Result(false, "", "length");
        long ts;
        if (Epoch(chat, "time") is { } fromTime)
        {
            if (!ToEpochSeconds(fromTime, out ts))
                return new Result(false, "", "type");
        }
        else if (Epoch(chat, "ts") is { } fromTs)
        {
            if (!ToEpochSeconds(fromTs, out ts))
                return new Result(false, "", "type");
        }
        else
        {
            ts = nowSeconds;
        }

        if (ts < 0 || ts > TsMax)
            return new Result(false, "", "type");

        var payload = new JsonObject
        {
            ["id"] = id.ToLowerInvariant(),
            ["workspaceKey"] = key,
            ["sender"] = sender,
            ["text"] = text,
            ["ts"] = ts,
            ["threadKey"] = "room"
        };
        return new Result(true, payload.ToJsonString(JsonUtil.Opts), "");
    }

    private static long? Epoch(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var seconds))
            return seconds;
        if (value.TryGetValue<int>(out var small))
            return small;
        return null;
    }

    /// <summary>
    /// Seconds pass through. A value above <see cref="TsMax"/> and at most
    /// <see cref="TsMaxMillis"/> is Unix milliseconds and becomes whole seconds.
    /// </summary>
    private static bool ToEpochSeconds(long raw, out long seconds)
    {
        if (raw >= 0 && raw <= TsMax)
        {
            seconds = raw;
            return true;
        }

        if (raw > TsMax && raw <= TsMaxMillis)
        {
            seconds = raw / 1000L;
            return seconds >= 0 && seconds <= TsMax;
        }

        seconds = 0;
        return false;
    }
}

internal sealed class DelegateHivemindMirror(Action<string> offer, Action<string> log) : IHivemindMirror
{
    public void Offer(JsonObject chat, string channelFallback)
    {
        var built = MirrorPayload.Build(chat, channelFallback, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (!built.Ok)
        {
            log("hivemind message mirror refused (" + built.Reason + ")");
            return;
        }

        offer(built.Json);
    }
}

internal sealed class NodeHivemindMirror : IHivemindMirror
{
    private const int MaxQueued = 32;
    private readonly Func<string, string?> _env;
    private readonly Action<string> _log;
    private readonly string? _script;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private int _scriptMissing;

    private NodeHivemindMirror(Func<string, string?> env, Action<string> log, string? script)
    {
        _env = env;
        _log = log;
        _script = script;
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "hivemind-mirror"
        };
        thread.Start();
    }

    public static NodeHivemindMirror Start(Func<string, string?> env, Action<string> log) =>
        new(env, log, FindScript(env));

    public void Offer(JsonObject chat, string channelFallback)
    {
        var built = MirrorPayload.Build(chat, channelFallback, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (!built.Ok)
        {
            _log("hivemind message mirror refused (" + built.Reason + ")");
            return;
        }

        lock (_gate)
        {
            if (_pending.Count >= MaxQueued)
            {
                _log("hivemind message mirror refused (queue)");
                return;
            }

            _pending.Enqueue(built.Json);
        }

        _signal.Release();
    }

    private void Loop()
    {
        while (true)
        {
            try
            {
                _signal.Wait();
            }
            catch (Exception)
            {
                return;
            }

            if (!_pending.TryDequeue(out var json))
                continue;
            try
            {
                RunOne(json);
            }
            catch (Exception)
            {
                _log("hivemind message mirror failed (error)");
            }
        }
    }

    private void RunOne(string json)
    {
        if (string.IsNullOrEmpty(_script) || !File.Exists(_script))
        {
            if (Interlocked.Exchange(ref _scriptMissing, 1) == 0)
                _log("hivemind message mirror failed (script)");
            return;
        }

        var node = _env("HIVEMIND_MIRROR_NODE");
        if (string.IsNullOrWhiteSpace(node))
            node = "node";
        var psi = new ProcessStartInfo
        {
            FileName = node.Trim(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(_script);
        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
            {
                _log("hivemind message mirror failed (spawn)");
                return;
            }
        }
        catch (Exception)
        {
            _log("hivemind message mirror failed (spawn)");
            return;
        }

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        try
        {
            proc.StandardInput.Write(json);
            proc.StandardInput.Close();
        }
        catch (Exception)
        {
            TryKill(proc);
            _log("hivemind message mirror failed (stdin)");
            return;
        }

        if (!proc.WaitForExit(35_000))
        {
            TryKill(proc);
            try { proc.WaitForExit(2_000); }
            catch (Exception) { /* already gone */ }
            _log("hivemind message mirror failed (timeout)");
            return;
        }

        // Drain so the process can leave. Forward only the helper's own status
        // lines. A nonzero exit that printed none of those still gets one log.
        _ = stdout.GetAwaiter().GetResult();
        LogHelperResult(stderr.GetAwaiter().GetResult(), proc.ExitCode, _log);
    }

    internal static void LogHelperResult(string stderr, int exitCode, Action<string> log)
    {
        var forwarded = false;
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!SafeLine(trimmed))
                continue;
            log(trimmed);
            forwarded = true;
        }

        if (exitCode != 0 && !forwarded)
            log("hivemind message mirror failed (exit)");
    }

    private static bool SafeLine(string line) =>
        Regex.IsMatch(
            line,
            "^hivemind [a-z0-9 -]+ (failed|refused) \\([A-Za-z0-9_-]+\\)$",
            RegexOptions.CultureInvariant);

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // already gone
        }
    }

    private static string? FindScript(Func<string, string?> env)
    {
        var over = env("HIVEMIND_MIRROR_SCRIPT");
        if (!string.IsNullOrWhiteSpace(over))
            return over.Trim();
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "web", "muse", "messageMirror.js");
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }

        return null;
    }
}
