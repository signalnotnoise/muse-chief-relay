using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatBridge;

/// <summary>
/// Public trip codes for voizle-text-relay. The relay stores the <c>!</c> prefix as part of the id.
/// Allowlists in this bridge strip that prefix, so inbound trips and own-trip identity use the same form.
/// </summary>
internal static partial class PublicTrip
{
    // Same shape Muse sends: six characters, then the bridge puts "!" in front on the wire.
    [GeneratedRegex(@"^[A-Za-z0-9+/]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SixCharBody();

    /// <summary>Trim and drop leading <c>!</c>. Empty becomes null. This is the allowlist form.</summary>
    public static string? Canonical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trip = raw.Trim().TrimStart('!');
        return trip.Length == 0 ? null : trip;
    }

    /// <summary>
    /// Value for a v1 <c>join.trip</c>. A public code is sent as <c>!XXXXXX</c>.
    /// A password, <c>nick#secret</c>, or anything else is not a public trip and is not sent.
    /// </summary>
    public static string? ForJoin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        if (trimmed.Contains('#') || trimmed.Any(char.IsWhiteSpace))
            return null;
        var body = trimmed.StartsWith('!') ? trimmed[1..] : trimmed;
        return SixCharBody().IsMatch(body) ? "!" + body : null;
    }
}

/// <summary>Maps an outbox envelope onto a voizle-text-relay v1 client frame.</summary>
internal static class VoizleWire
{
    public static JsonObject FromOutbox(JsonObject payload)
    {
        var kind = Json.Str(payload, "type") ?? Json.Str(payload, "cmd");
        if (kind == "chat" || (kind is null && Json.Str(payload, "text") is not null))
            return Chat(Json.Str(payload, "text") ?? "");
        if (kind is "ping" or "leave")
            return new JsonObject { ["v"] = 1, ["type"] = kind };
        if (kind == "nick" && Json.Str(payload, "nick") is { } nick)
            return new JsonObject { ["v"] = 1, ["type"] = "nick", ["nick"] = nick };
        if (kind == "join")
        {
            var join = new JsonObject
            {
                ["v"] = 1,
                ["type"] = "join",
                ["room"] = Json.Str(payload, "room") ?? Json.Str(payload, "channel") ?? "",
                ["nick"] = Json.Str(payload, "nick") ?? ""
            };
            if (PublicTrip.ForJoin(Json.Str(payload, "trip")) is { } trip)
                join["trip"] = trip;
            return join;
        }

        if (Json.Str(payload, "type") is not null && payload.ContainsKey("v"))
            return (JsonObject)payload.DeepClone();

        var copy = (JsonObject)payload.DeepClone();
        copy["v"] = 1;
        if (Json.Str(copy, "type") is null && kind is not null)
            copy["type"] = kind;
        copy.Remove("cmd");
        copy.Remove("pass");
        return copy;
    }

    public static JsonObject Chat(string text) => new()
    {
        ["v"] = 1,
        ["type"] = "chat",
        ["text"] = text
    };
}
