using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBridge;

/// <summary>
/// Collects the fragments of one WebSocket message and decodes UTF-8 once, at end of message, so a
/// multi-byte character split across two fragments comes out intact. A message larger than
/// <c>maxBytes</c> is dropped whole rather than growing memory without bound.
/// </summary>
internal sealed class MessageAssembler(int maxBytes)
{
    private readonly MemoryStream _buf = new();
    private bool _overflow;

    /// <returns>The decoded message at end of message; null while a message is incomplete or when it was dropped.</returns>
    public string? Append(ReadOnlySpan<byte> chunk, bool endOfMessage, out bool dropped)
    {
        dropped = false;
        if (!_overflow)
        {
            if (_buf.Length + chunk.Length > maxBytes)
            {
                _overflow = true;
                _buf.SetLength(0);
            }
            else
            {
                _buf.Write(chunk);
            }
        }

        if (!endOfMessage)
            return null;

        if (_overflow)
        {
            _overflow = false;
            dropped = true;
            return null;
        }

        var text = Encoding.UTF8.GetString(_buf.GetBuffer(), 0, (int)_buf.Length);
        _buf.SetLength(0);
        return text;
    }
}

/// <summary>One inbound frame: the object (if it is one), its cmd, and a redacted copy for the inbox log.</summary>
internal sealed record InboundFrame(JsonNode LogNode, JsonObject? Object, string? Cmd)
{
    public static InboundFrame Parse(string raw)
    {
        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            // not JSON: logged as raw below
        }

        // Anything that isn't a JSON object (unparsable text, arrays, bare values) is logged as
        // {"raw": "..."} and otherwise ignored. It never ends the session.
        if (node is not JsonObject obj)
            return new InboundFrame(new JsonObject { ["raw"] = raw }, null, null);

        // v1 frames say "type" and "room". Persist cmd/channel too so watch, hook, and
        // status keep seeing a chat, and store the trip in the same form as allowlists.
        Normalize(obj);
        var cmd = Json.Str(obj, "cmd");
        return new InboundFrame(LogRedaction.Inbound(obj, cmd), obj, cmd);
    }

    /// <summary>A log copy of an outbound v1 frame: <c>cmd</c>/<c>channel</c> filled in, trip canonical.</summary>
    public static JsonObject ForLog(JsonObject wire)
    {
        var copy = (JsonObject)wire.DeepClone();
        Normalize(copy);
        return copy;
    }

    /// <summary>
    /// Copy <c>type</c> into <c>cmd</c> and <c>room</c> into <c>channel</c> when those hack.chat names
    /// are absent, and store <c>trip</c> without a leading <c>!</c>.
    /// </summary>
    internal static void Normalize(JsonObject obj)
    {
        if (Json.Str(obj, "cmd") is null && Json.Str(obj, "type") is { } type)
            obj["cmd"] = type;
        if (Json.Str(obj, "channel") is null && Json.Str(obj, "room") is { } room)
            obj["channel"] = room;
        if (Json.Str(obj, "trip") is { } trip)
        {
            var canonical = PublicTrip.Canonical(trip);
            if (canonical is null)
                obj.Remove("trip");
            else
                obj["trip"] = canonical;
        }
    }
}

internal static class LogRedaction
{
    public const string Redacted = "<redacted>";

    /// <summary>
    /// Copy of an inbound frame for inbox.jsonl. hack.chat's <c>session</c> frame carries a session token;
    /// it is replaced with a marker.
    /// </summary>
    public static JsonNode Inbound(JsonObject obj, string? cmd)
    {
        var copy = (JsonObject)obj.DeepClone();
        if (cmd == "session" && copy.ContainsKey("token"))
            copy["token"] = Redacted;
        // Owner material is never an inbox line. `binding` is the designed §4 token
        // (not issued by the deployed server). `pass` / `password` are the §11 owner secret.
        RedactOwnerFields(copy);
        return copy;
    }

    /// <summary>Copy of an outbound frame for inbox.jsonl, with any <c>pass</c> field redacted.</summary>
    public static JsonNode Outbound(JsonNode payload)
    {
        var copy = payload.DeepClone();
        if (copy is JsonObject o)
            RedactOwnerFields(o);
        return copy;
    }

    /// <summary>
    /// Error text for an outbox line that produced no frames. Character count only.
    /// The raw line is never included: a rejected line can still hold a <c>pass</c>,
    /// token, or other secret, and this string is written to inbox.jsonl.
    /// </summary>
    public static string DroppedOutboxLine(string line) =>
        $"outbox: dropped malformed line ({line.Trim().Length} chars)";

    /// <summary>Replace owner-secret fields in place. Does not touch public trips.</summary>
    public static void RedactOwnerFields(JsonObject obj)
    {
        foreach (var key in new[] { "pass", "password", "binding" })
        {
            if (obj.ContainsKey(key))
                obj[key] = Redacted;
        }
    }
}

internal static class OutboxPayload
{
    /// <summary>
    /// Turn one outbox line into zero or more frames. A JSON object with a string <c>cmd</c> is sent
    /// as is; an object with a string <c>text</c> becomes a chat. A line may hold several
    /// concatenated JSON objects (two appends that lost the newline between them); each one becomes
    /// its own frame, but only when every top-level value is an accepted envelope. One non-sendable
    /// value — plain text, a protocol object, an array, a bare value, truncated input, trailing
    /// garbage, or a chat whose text is CLI/shell probe leftover — drops the whole line, including
    /// any valid envelopes beside it. Fail closed: the channel must never see raw outbox bytes, and
    /// a mixed line must not send a partial result. Blank lines give no frames (nothing to send).
    /// </summary>
    public static IReadOnlyList<JsonObject> BuildAll(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line.Trim());
        if (bytes.Length == 0)
            return Array.Empty<JsonObject>();

        // Split the line into top-level JSON value ranges. A line normally holds one
        // envelope; two appends that lost the newline between them hold two.
        // (JsonDocument.ParseValue is single-value by design, so the split is done
        // with the tokenizer directly.)
        var ranges = SplitTopLevelValues(bytes);
        if (ranges is null)
            return Array.Empty<JsonObject>(); // malformed: fail closed, send nothing

        var payloads = new List<JsonObject>();
        foreach (var (start, length) in ranges)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(bytes.AsSpan(start, length));
            }
            catch (JsonException)
            {
                return Array.Empty<JsonObject>(); // unreachable in practice; fail closed anyway
            }
            // A parsed value that is not an accepted envelope fails the whole line.
            // Skipping it and returning the neighbors would still send a chat from mixed
            // input such as {"cmd":"chat","text":"a"}{"type":"result","body":"x"}.
            if (node is not JsonObject obj || FromObject(obj) is not { } payload)
                return Array.Empty<JsonObject>();
            payloads.Add(payload);
        }
        return payloads;
    }

    /// <summary>
    /// Byte ranges of each top-level JSON value in <paramref name="bytes"/>, or null when the
    /// input is not well-formed JSON (truncated mid-value, trailing garbage, ...).
    /// A small hand scanner is used instead of <see cref="Utf8JsonReader"/> because the reader
    /// is single-document by design: it throws once a first top-level value is complete.
    /// Each range is re-parsed strictly by the caller, so the scanner only has to find
    /// plausible value boundaries (string-aware, so braces inside strings don't split).
    /// </summary>
    private static List<(int Start, int Length)>? SplitTopLevelValues(byte[] bytes)
    {
        var ranges = new List<(int Start, int Length)>();
        int i = 0, n = bytes.Length;
        while (i < n)
        {
            while (i < n && IsJsonWhitespace(bytes[i])) i++;
            if (i >= n) break;
            int end = ScanOneValue(bytes, i);
            if (end < 0) return null; // malformed or truncated: fail closed
            ranges.Add((i, end - i));
            i = end;
        }
        return ranges;
    }

    private static bool IsJsonWhitespace(byte b) =>
        b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    /// <summary>
    /// Scans one JSON value starting at <paramref name="bytes"/>[<paramref name="i"/>]
    /// (which is not whitespace). Returns the exclusive end offset, or -1 when the
    /// value is malformed or truncated.
    /// </summary>
    private static int ScanOneValue(byte[] bytes, int i)
    {
        int n = bytes.Length;
        byte b = bytes[i];
        if (b == (byte)'{' || b == (byte)'[')
        {
            int depth = 0;
            bool inString = false;
            for (int j = i; j < n; j++)
            {
                byte c = bytes[j];
                if (inString)
                {
                    if (c == (byte)'\\') { j++; continue; } // skip the escaped char
                    if (c == (byte)'"') inString = false;
                }
                else if (c == (byte)'"') inString = true;
                else if (c == (byte)'{' || c == (byte)'[') depth++;
                else if (c == (byte)'}' || c == (byte)']')
                {
                    depth--;
                    if (depth == 0) return j + 1;
                    if (depth < 0) return -1;
                }
            }
            return -1; // truncated
        }
        if (b == (byte)'"')
        {
            for (int j = i + 1; j < n; j++)
            {
                byte c = bytes[j];
                if (c == (byte)'\\') { j++; continue; }
                if (c == (byte)'"') return j + 1;
                if (c < 0x20) return -1; // control character: invalid JSON
            }
            return -1; // unterminated string
        }
        // true / false / null / number: run to the next boundary byte.
        int k = i;
        while (k < n && !IsJsonWhitespace(bytes[k]) && bytes[k] != (byte)'{' && bytes[k] != (byte)'}'
            && bytes[k] != (byte)'[' && bytes[k] != (byte)']' && bytes[k] != (byte)'"'
            && bytes[k] != (byte)',' && bytes[k] != (byte)':')
            k++;
        return k > i ? k : -1;
    }

    private static JsonObject? FromObject(JsonObject o)
    {
        // A string cmd is sent as-is, except a chat whose text is probe leftover: that envelope
        // is not sendable, so the caller fail-closes the whole line. Other commands (emote, join)
        // are unchanged even when their text looks like a flag.
        if (Json.Str(o, "cmd") is { } cmd)
        {
            if (cmd == "chat" && CliProbeText.IsFragment(Json.Str(o, "text")))
                return null;
            return o;
        }
        if (Json.Str(o, "text") is { } text)
        {
            if (CliProbeText.IsFragment(text))
                return null;
            return new JsonObject { ["cmd"] = "chat", ["text"] = text };
        }
        return null;
    }
}

/// <summary>
/// Accidental CLI and shell probe text that wake hooks have posted as chat
/// (<c>hc say --help</c>, bad quoting). A normal sentence that mentions a flag or a price is not a fragment.
/// </summary>
internal static class CliProbeText
{
    /// <summary>
    /// True when <paramref name="text"/> is probe leftover rather than a chat message.
    /// Null, blank, and ordinary sentences are not fragments.
    /// </summary>
    public static bool IsFragment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = StripOneQuoteLayer(text.Trim());
        if (s.Length == 0)
            return false;

        if (s is "--help" or "-h" or "--" or "help")
            return true;

        return IsSingleFlagToken(s) || IsBareShellLeftover(s);
    }

    /// <summary>One matching pair of <c>'</c> or <c>"</c> around the whole string, then trim again.</summary>
    private static string StripOneQuoteLayer(string s)
    {
        if (s.Length >= 2 && s[0] == s[^1] && s[0] is '"' or '\'')
            return s[1..^1].Trim();
        return s;
    }

    /// <summary>
    /// One flag token: a leading <c>-</c> or <c>--</c>, no spaces or tabs, and a non-empty body of
    /// letters, digits, <c>-</c>, and <c>_</c>. A lone <c>-</c> is not a flag. <c>--</c> is an exact
    /// match in <see cref="IsFragment"/>, not this rule (its body is empty).
    /// </summary>
    private static bool IsSingleFlagToken(string s)
    {
        if (s.Length < 2 || s[0] != '-')
            return false;
        if (s.Contains(' ') || s.Contains('\t'))
            return false;

        var body = s.StartsWith("--", StringComparison.Ordinal) ? s[2..] : s[1..];
        if (body.Length == 0)
            return false;

        foreach (var c in body)
        {
            if (!IsFlagBodyChar(c))
                return false;
        }
        return true;
    }

    private static bool IsFlagBodyChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';

    /// <summary><c>$</c> plus only letters, digits, and <c>_</c>, such as <c>$REPLY</c> or <c>$1</c>.</summary>
    private static bool IsBareShellLeftover(string s)
    {
        if (s.Length < 2 || s[0] != '$')
            return false;
        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
                return false;
        }
        return true;
    }
}

internal static class Json
{
    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
