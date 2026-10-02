using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatBridge;

/// <summary>One inbound room chat the router can see. Not a side-chat line.</summary>
internal sealed record RoomMessage(string Room, string From, string? Trip, string Text, long? Ts, string? ServerId)
{
    public static RoomMessage FromFrame(JsonObject obj, string roomFallback)
    {
        long? ts = null;
        if (obj["time"] is JsonValue time && time.TryGetValue<long>(out var seconds))
            ts = seconds;
        else if (obj["ts"] is JsonValue stamp && stamp.TryGetValue<long>(out var tsSeconds))
            ts = tsSeconds;

        return new RoomMessage(
            Json.Str(obj, "channel") ?? roomFallback,
            Json.Str(obj, "nick") ?? "",
            Json.Str(obj, "trip"),
            Json.Str(obj, "text") ?? "",
            ts,
            Json.Str(obj, "id"));
    }
}

/// <summary>Explicit tags in one room line. Bare words are not mentions.</summary>
internal sealed record MentionParseResult(
    IReadOnlyList<AgentRecord> Agents,
    bool ExplicitFanout,
    int Hop,
    string? Parent,
    string? Root);

internal static class MentionParse
{
    private static readonly Regex AtNick = new(
        @"(?<![\p{L}\p{N}_.\-@])@(?<nick>[\p{L}\p{N}_\-]+)(?![\p{L}\p{N}_\-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FanoutToken = new(
        @"(?i)(?:^|\s)!fanout(?=$|\s)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static MentionParseResult Parse(string? text, AgentDirectory directory)
    {
        if (string.IsNullOrWhiteSpace(text) || directory.Agents.Count == 0)
            return new MentionParseResult(Array.Empty<AgentRecord>(), false, 0, null, null);

        var raw = text.Trim();
        var fanout = false;
        var hop = 0;
        string? parent = null;
        string? root = null;
        var scan = raw;
        IReadOnlyList<string> addressed = Array.Empty<string>();

        if (raw.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(raw) is JsonObject obj)
                {
                    fanout = obj["fanout"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
                    if (obj["hop"] is JsonValue hopValue && hopValue.TryGetValue<int>(out var parsedHop))
                        hop = parsedHop;
                    else if (obj["hop"] is JsonValue hopLong && hopLong.TryGetValue<long>(out var parsedLong))
                        hop = parsedLong > int.MaxValue ? int.MaxValue : (int)parsedLong;
                    parent = Token(obj["parent"]);
                    root = Token(obj["root"]);
                    addressed = Tokens(obj["to"]);
                    scan = Json.Str(obj, "text") ?? "";
                }
            }
            catch (JsonException)
            {
                // Plain text that happens to start with a brace.
            }
        }

        if (!fanout && FanoutToken.IsMatch(raw))
            fanout = true;

        var found = new List<AgentRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(AgentRecord? agent)
        {
            if (agent is null || !seen.Add(agent.Id))
                return;
            found.Add(agent);
        }

        foreach (var token in addressed)
            Add(directory.Find(token));

        foreach (Match match in AtNick.Matches(scan.Length > 0 ? scan : raw))
            Add(directory.FindByNick(match.Groups["nick"].Value));

        // Protocol shortcut, only at the start of the plain text: TASK to <nick>:
        var taskText = scan.Length > 0 ? scan : raw;
        foreach (var agent in directory.Agents)
        {
            foreach (var nick in agent.Nicks)
            {
                if (Regex.IsMatch(taskText, $@"^TASK\s+to\s+@?{Regex.Escape(nick)}\s*:",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    Add(agent);
            }
        }

        return new MentionParseResult(found, fanout, hop < 0 ? 0 : hop, parent, root);
    }

    private static string? Token(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            return text.Trim();
        return null;
    }

    private static IReadOnlyList<string> Tokens(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var one) && !string.IsNullOrWhiteSpace(one):
                return [one.Trim()];
            case JsonArray array:
                return array
                    .Select(item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")
                    .Where(s => s.Length > 0)
                    .ToList();
            default:
                return Array.Empty<string>();
        }
    }
}

internal static class MessageIds
{
    /// <summary>
    /// Stable id for one room chat. A server id wins. Otherwise the hash of room, sender, trip,
    /// timestamp, and text. Identical lines with no server id and no timestamp collapse, which is
    /// how replay is suppressed.
    /// </summary>
    public static string Source(RoomMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.ServerId))
            return "srv:" + message.ServerId.Trim();

        var raw = string.Join('\n',
            message.Room ?? "",
            message.From ?? "",
            message.Trip ?? "",
            message.Ts?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            message.Text ?? "");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "msg:" + Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    public static string Delivery(string agentId, string sourceId) => agentId + ":" + sourceId;
}
