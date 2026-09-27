using System.Text.RegularExpressions;

namespace Chief.Knowledge;

/// <summary>
/// Fail-closed scan for notes that must not enter a public index.
/// Findings name the kind of secret, never the secret itself.
/// </summary>
public static partial class PrivacyGuard
{
    public static IReadOnlyList<Issue> Scan(string path, string text)
    {
        var found = new List<Issue>();
        void Hit(Regex regex, string reason)
        {
            foreach (Match match in regex.Matches(text))
            {
                if (!match.Success)
                    continue;
                found.Add(new Issue(path, $"line {LineOf(text, match.Index)}: {reason}"));
            }
        }

        Hit(Bearer(), "looks like a bearer key");
        Hit(PasswordAssignment(), "looks like a password");
        Hit(TokenAssignment(), "looks like a token or session secret");
        Hit(TripPassword(), "looks like a trip password");
        Hit(KnownPrefix(), "looks like an API token");
        Hit(NickPassword(), "looks like a nick#password trip secret");
        Hit(UrlCredentials(), "URL contains credentials");

        // A board file is boards/<64 lowercase hex>.jsonl, the SHA-256 of the
        // trimmed channel. Any other boards/<name>.jsonl is a raw channel name.
        // The documentation placeholder is the one exception; it names no channel.
        foreach (Match match in BoardPath().Matches(text))
        {
            var name = match.Groups[1].Value;
            if (IsBoardHash(name) || name == "<sha256(trimmed channel)>")
                continue;
            found.Add(new Issue(path, $"line {LineOf(text, match.Index)}: looks like a raw channel name in a board path"));
        }

        foreach (Match match in VisibilityLine().Matches(text))
        {
            var value = match.Groups[1].Value;
            if (!string.Equals(value, "public", StringComparison.Ordinal))
                found.Add(new Issue(path, $"line {LineOf(text, match.Index)}: visibility is {value}, not public"));
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

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]{8,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    // A quote may sit between the key and ':' ("password": "..."). '_' is a
    // boundary so my_password matches; a letter still blocks compass and bypass.
    [GeneratedRegex(@"(?im)(?:^|[^A-Za-z0-9])(?:password|passwd|pwd|pass)['""]?\s*[:=]\s*['""]?[^\s'""]{4,}")]
    private static partial Regex PasswordAssignment();

    [GeneratedRegex(@"(?im)(?:^|[^A-Za-z0-9])(?:token|secret|api[_-]?key|access[_-]?token|session[_-]?token)['""]?\s*[:=]\s*['""]?[^\s'""]{8,}")]
    private static partial Regex TokenAssignment();

    [GeneratedRegex(@"(?im)\btrip[_-]?password\b['""]?\s*[:=]\s*['""]?[^\s'""]{4,}")]
    private static partial Regex TripPassword();

    [GeneratedRegex(@"\b(?:ghp_|gho_|ghu_|ghs_|ghr_|github_pat_|sk-|xox[baprs]-|AKIA[0-9A-Z]{16})[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
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
