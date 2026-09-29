using System.Text.RegularExpressions;

namespace Chief.Knowledge;

/// <summary>
/// Fail-closed scan for notes that must not enter a public index.
/// Findings name the kind of secret, never the secret itself.
/// The same secret patterns run on the note text and on its relative path.
/// When the path itself matches, every finding from that scan uses
/// <see cref="RedactedPath"/> so the secret is not copied into <see cref="Issue.Path"/>.
/// </summary>
public static partial class PrivacyGuard
{
    /// <summary>Stand-in for a relative path that itself matched a secret pattern.</summary>
    public const string RedactedPath = "[redacted-path]";

    public static IReadOnlyList<Issue> Scan(string path, string text)
    {
        var found = new List<Issue>();
        var reportPath = path;
        void Secrets(string subject, Func<int, string> locate)
        {
            void Hit(Regex regex, string reason, int quotedMinimum = 0)
            {
                foreach (Match match in regex.Matches(subject))
                {
                    if (!match.Success)
                        continue;
                    // Quoted assignments capture the whole span, escapes included.
                    // A span shorter than the minimum is not a finding; the regex
                    // still matches it so a short quoted value cannot fall through
                    // to the unquoted alternative.
                    var quoted = match.Groups["q"];
                    if (quotedMinimum > 0 && quoted.Success && quoted.Length < quotedMinimum)
                        continue;
                    found.Add(new Issue(reportPath, locate(match.Index) + reason));
                }
            }

            Hit(Bearer(), "looks like a bearer key");
            Hit(PasswordAssignment(), "looks like a password", 4);
            Hit(TokenAssignment(), "looks like a token or session secret", 8);
            Hit(TripPassword(), "looks like a trip password", 4);
            Hit(KnownPrefix(), "looks like an API token");
            Hit(NickPassword(), "looks like a nick#password trip secret");
            Hit(UrlCredentials(), "URL contains credentials");

            // A board file is boards/<64 lowercase hex>.jsonl, the SHA-256 of the
            // trimmed channel. Any other boards/<name>.jsonl is a raw channel name.
            // The documentation placeholder is the one exception; it names no channel.
            foreach (Match match in BoardPath().Matches(subject))
            {
                var name = match.Groups[1].Value;
                if (IsBoardHash(name) || name == "<sha256(trimmed channel)>")
                    continue;
                found.Add(new Issue(reportPath, locate(match.Index) + "looks like a raw channel name in a board path"));
            }
        }

        // File names and directory segments are public the moment the note is
        // added. A clean body must not launder a secret that sits in the path.
        if (!string.IsNullOrEmpty(path))
            Secrets(path.Replace('\\', '/'), _ => "path: ");

        // The path matched. Rewrite findings already recorded, and report the
        // body against the same stand-in so a later line cannot repeat the path.
        if (found.Count > 0)
        {
            reportPath = RedactedPath;
            for (var i = 0; i < found.Count; i++)
                found[i] = new Issue(RedactedPath, found[i].Message);
        }

        Secrets(text, index => $"line {LineOf(text, index)}: ");

        foreach (Match match in VisibilityLine().Matches(text))
        {
            var value = match.Groups[1].Value;
            if (!string.Equals(value, "public", StringComparison.Ordinal))
                found.Add(new Issue(reportPath, $"line {LineOf(text, match.Index)}: visibility is {value}, not public"));
        }

        return found;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]{8,}={0,2}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    // A quote may sit between the key and ':' ("password": "..."). '_' is a
    // boundary so my_password matches; a letter still blocks compass and bypass.
    // A quoted value is the whole span inside the quotes. A backslash escapes
    // the next character, so an escaped quote stays inside the span instead of
    // closing it. The minimum length applies to that span (group q), so a short
    // first word cannot hide the rest. An unquoted value still ends at
    // whitespace. Password and trip values need 4 characters; token and
    // api_key values need 8.
    [GeneratedRegex(@"(?im)(?:^|[^A-Za-z0-9])(?:password|passwd|pwd|pass)['""]?\s*[:=]\s*(?:""(?<q>(?:\\.|[^""\\])*)""|'(?<q>(?:\\.|[^'\\])*)'|['""]?[^\s'""]{4,})")]
    private static partial Regex PasswordAssignment();

    [GeneratedRegex(@"(?im)(?:^|[^A-Za-z0-9])(?:token|secret|api[_-]?key|access[_-]?token|session[_-]?token)['""]?\s*[:=]\s*(?:""(?<q>(?:\\.|[^""\\])*)""|'(?<q>(?:\\.|[^'\\])*)'|['""]?[^\s'""]{8,})")]
    private static partial Regex TokenAssignment();

    [GeneratedRegex(@"(?im)\btrip[_-]?password\b['""]?\s*[:=]\s*(?:""(?<q>(?:\\.|[^""\\])*)""|'(?<q>(?:\\.|[^'\\])*)'|['""]?[^\s'""]{4,})")]
    private static partial Regex TripPassword();

    // AKIA key IDs are exactly 20 chars (AKIA + 16); the old shared {8,} tail
    // let a bare 20-char ID pass clean, so AKIA gets its own alternative.
    [GeneratedRegex(@"\b(?:(?:ghp_|gho_|ghu_|ghs_|ghr_|github_pat_|sk-|xox[baprs]-)[A-Za-z0-9_\-]{8,}|AKIA[A-Z0-9]{16}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex KnownPrefix();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9_]{0,23}#(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NickPassword();

    [GeneratedRegex(@"://[^\s/@]+:[^\s/@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?im)^visibility:\s*(\S+)\s*$")]
    private static partial Regex VisibilityLine();

    // The placeholder contains a space, so it is matched literally. Everything
    // else up to .jsonl is a single non-whitespace name.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])boards/(<sha256\(trimmed channel\)>|\S+)\.jsonl", RegexOptions.CultureInvariant)]
    private static partial Regex BoardPath();

    private static bool IsBoardHash(string name)
    {
        if (name.Length != 64)
            return false;
        foreach (var c in name)
        {
            if (c is >= '0' and <= '9' or >= 'a' and <= 'f')
                continue;
            return false;
        }
        return true;
    }
}
