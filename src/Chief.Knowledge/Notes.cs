using System.Globalization;
using System.Text;

namespace Chief.Knowledge;

public sealed class Issue
{
    public Issue(string path, string message)
    {
        Path = path;
        Message = message;
    }

    public string Path { get; }
    public string Message { get; }
}

public sealed class NoteDocument
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required string Source { get; init; }
    public required IReadOnlyList<string> Authors { get; init; }
    public required DateOnly Created { get; init; }
    public IReadOnlyList<string> Supersedes { get; init; } = [];
    public IReadOnlyList<string> Links { get; init; } = [];
    public required string Visibility { get; init; }
    public bool Important { get; init; }
    public required string Body { get; init; }
    public required string Path { get; init; }

    public string EmbeddingInput => Title + "\n" + Summary + "\n" + Body;
}

public sealed class Corpus
{
    public required IReadOnlyList<NoteDocument> Notes { get; init; }
    public required IReadOnlyList<Issue> Validation { get; init; }
    public required IReadOnlyList<Issue> Privacy { get; init; }
    public required IReadOnlyList<Issue> Warnings { get; init; }

    public int FailureCode => Privacy.Count > 0 ? 1 : Validation.Count > 0 ? 2 : 0;
}

public static class NoteIds
{
    public static bool IsValid(string id) =>
        id.Length is >= 1 and <= 80 && System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9]+(?:-[a-z0-9]+)*$");

    public static string Slug(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length > 80)
            s = s[..80].Trim('-');
        return s;
    }
}

public static class NoteWriter
{
    public static string Render(NoteDocument note)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        Line(sb, "id", note.Id);
        Line(sb, "title", note.Title);
        Line(sb, "summary", note.Summary);
        sb.Append("tags: ").Append(Inline(note.Tags)).Append('\n');
        Line(sb, "source", note.Source);
        sb.Append("authors: ").Append(Inline(note.Authors)).Append('\n');
        Line(sb, "created", note.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (note.Supersedes.Count > 0)
            sb.Append("supersedes: ").Append(Inline(note.Supersedes)).Append('\n');
        if (note.Links.Count > 0)
            sb.Append("links: ").Append(Inline(note.Links)).Append('\n');
        Line(sb, "visibility", note.Visibility);
        if (note.Important)
            Line(sb, "flagged", "important");
        sb.Append("---\n\n");
        sb.Append(note.Body.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(": ").Append(value).Append('\n');

    private static string Inline(IReadOnlyList<string> items) =>
        "[" + string.Join(", ", items) + "]";
}

public static class NoteLoader
{
    public static readonly string[] Authors = ["chief", "fuse", "alex"];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Corpus Load(string knowledgeDir)
    {
        var validation = new List<Issue>();
        var privacy = new List<Issue>();
        var warnings = new List<Issue>();
        var notes = new List<NoteDocument>();

        if (!Directory.Exists(knowledgeDir))
        {
            validation.Add(new Issue(knowledgeDir, "knowledge directory does not exist"));
            return new Corpus { Notes = notes, Validation = validation, Privacy = privacy, Warnings = warnings };
        }

        foreach (var path in Directory.EnumerateFiles(knowledgeDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(knowledgeDir, path);
            if (rel.StartsWith(".index" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || rel == ".index")
                continue;
            if (rel is "README.md")
            {
                ScanFile(path, rel, privacy);
                continue;
            }

            var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (top.StartsWith('.'))
            {
                validation.Add(new Issue(rel, "unexpected hidden file under knowledge/"));
                ScanFile(path, rel, privacy);
                continue;
            }

            if (!rel.EndsWith(".md", StringComparison.Ordinal) || rel.Contains(Path.DirectorySeparatorChar) || rel.Contains(Path.AltDirectorySeparatorChar))
            {
                validation.Add(new Issue(rel, "only README.md and one <id>.md per note belong under knowledge/"));
                ScanFile(path, rel, privacy);
                continue;
            }

            string text;
            try
            {
                text = ReadUtf8(path);
            }
            catch (DecoderFallbackException)
            {
                privacy.Add(new Issue(rel, "not valid UTF-8; refusing to index"));
                continue;
            }

            privacy.AddRange(PrivacyGuard.Scan(rel, text));
            if (!TryParse(rel, text, out var note, out var errors))
            {
                validation.AddRange(errors);
                continue;
            }

            if (!string.Equals(Path.GetFileNameWithoutExtension(rel), note.Id, StringComparison.Ordinal))
                validation.Add(new Issue(rel, $"file name must be {note.Id}.md"));

            // PrivacyGuard already reports a `visibility:` line that is not public.
            // This catches a non-public value the line regex did not (for example
            // one with an internal space). A missing value is a validation error.
            if (!string.Equals(note.Visibility, "public", StringComparison.Ordinal)
                && !privacy.Exists(issue => issue.Path == rel && issue.Message.Contains("visibility is ", StringComparison.Ordinal)))
                privacy.Add(new Issue(rel, "visibility is not public"));

            notes.Add(note);
        }

        var byId = new Dictionary<string, NoteDocument>(StringComparer.Ordinal);
        foreach (var note in notes)
        {
            if (!byId.TryAdd(note.Id, note))
                validation.Add(new Issue(note.Path, $"duplicate id {note.Id}"));
        }

        foreach (var note in notes)
        {
            foreach (var id in note.Supersedes.Concat(note.Links))
            {
                if (!byId.ContainsKey(id))
                    warnings.Add(new Issue(note.Path, $"refers to {id}, which is not a note in this folder"));
            }
        }

        if (FindCycle(notes) is { } cycle)
            validation.Add(new Issue(cycle, "supersedes cycle; those notes would hide each other"));

        return new Corpus
        {
            Notes = notes,
            Validation = validation,
            Privacy = privacy,
            Warnings = warnings
        };
    }

    public static bool TryParse(string path, string text, out NoteDocument note, out List<Issue> errors)
    {
        note = null!;
        errors = new List<Issue>();
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || lines[0] != "---")
        {
            errors.Add(new Issue(path, "missing opening --- front matter"));
            return false;
        }

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var i = 1;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line == "---")
            {
                i++;
                break;
            }
            if (line.Length == 0)
            {
                errors.Add(new Issue(path, "blank line inside front matter"));
                return false;
            }
            if (line.StartsWith("  - ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
            {
                errors.Add(new Issue(path, "block lists must follow a key; use key: [a, b]"));
                return false;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                errors.Add(new Issue(path, $"front matter line is not key: value ({TrimPreview(line)})"));
                return false;
            }
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (raw.ContainsKey(key) || lists.ContainsKey(key))
            {
                errors.Add(new Issue(path, $"duplicate front matter key {key}"));
                return false;
            }
            if (value.Length == 0)
            {
                var items = new List<string>();
                while (i + 1 < lines.Length && lines[i + 1].StartsWith("  - ", StringComparison.Ordinal))
                {
                    i++;
                    items.Add(Unquote(lines[i][4..].Trim()));
                }
                if (items.Count == 0)
                {
                    errors.Add(new Issue(path, $"empty value for {key}"));
                    return false;
                }
                lists[key] = items;
                continue;
            }
            if (value.StartsWith('['))
            {
                if (!value.EndsWith(']'))
                {
                    errors.Add(new Issue(path, $"{key} list must be on one line"));
                    return false;
                }
                lists[key] = SplitList(value);
                continue;
            }
            raw[key] = Unquote(value);
        }
        if (i == lines.Length || (i > 0 && lines[i - 1] != "---"))
        {
            errors.Add(new Issue(path, "front matter is not closed with ---"));
            return false;
        }

        var body = string.Join('\n', lines[i..]).Trim();
        var id = Required(raw, errors, path, "id");
        var title = Required(raw, errors, path, "title");
        var summary = Required(raw, errors, path, "summary");
        var source = Required(raw, errors, path, "source");
        var createdRaw = Required(raw, errors, path, "created");
        var visibility = raw.TryGetValue("visibility", out var vis) ? vis : "";
        if (visibility.Length == 0)
            errors.Add(new Issue(path, "missing visibility"));

        foreach (var key in raw.Keys)
        {
            if (key is not ("id" or "title" or "summary" or "source" or "created" or "visibility" or "flagged"))
                errors.Add(new Issue(path, $"unknown front matter key {key}"));
        }
        foreach (var key in lists.Keys)
        {
            if (key is not ("tags" or "authors" or "supersedes" or "links"))
                errors.Add(new Issue(path, $"unknown front matter key {key}"));
        }

        if (title.Contains('\n') || summary.Contains('\n'))
            errors.Add(new Issue(path, "title and summary must be one line"));
        if (summary.Length > 240)
            errors.Add(new Issue(path, "summary must be one line of at most 240 characters"));

        if (!lists.TryGetValue("tags", out var tags) || tags.Count == 0)
            errors.Add(new Issue(path, "tags must list at least one tag"));
        tags ??= [];
        if (!lists.TryGetValue("authors", out var authors) || authors.Count == 0)
            errors.Add(new Issue(path, "authors must list at least one of chief, fuse, alex"));
        authors ??= [];
        lists.TryGetValue("supersedes", out var supersedes);
        lists.TryGetValue("links", out var links);
        supersedes ??= [];
        links ??= [];

        var normTags = new List<string>();
        foreach (var tag in tags)
        {
            var t = tag.Trim().ToLowerInvariant();
            if (!NoteIds.IsValid(t))
                errors.Add(new Issue(path, $"tag {tag} must be lowercase words separated by hyphens"));
            else if (normTags.Contains(t))
                errors.Add(new Issue(path, $"duplicate tag {t}"));
            else
                normTags.Add(t);
        }

        var normAuthors = new List<string>();
        foreach (var author in authors)
        {
            var a = author.Trim().ToLowerInvariant();
            if (!Authors.Contains(a))
                errors.Add(new Issue(path, $"author {author} must be chief, fuse, or alex"));
            else if (normAuthors.Contains(a))
                errors.Add(new Issue(path, $"duplicate author {a}"));
            else
                normAuthors.Add(a);
        }

        if (id.Length > 0 && !NoteIds.IsValid(id))
            errors.Add(new Issue(path, "id must be lowercase words separated by hyphens, at most 80 characters"));

        DateOnly created = default;
        if (createdRaw.Length > 0 && !DateOnly.TryParseExact(createdRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out created))
            errors.Add(new Issue(path, "created must be YYYY-MM-DD"));

        var normSource = source.Trim();
        if (normSource.Equals("alex", StringComparison.OrdinalIgnoreCase))
            normSource = "alex";
        else if (normSource.Equals("room", StringComparison.OrdinalIgnoreCase))
            normSource = "room";
        else if (!IsPublicHttps(normSource))
            errors.Add(new Issue(path, "source must be alex, room, or an https URL (never a channel name)"));

        if (Uri.TryCreate(normSource, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            errors.Add(new Issue(path, "source URL must not carry credentials"));

        bool important = false;
        if (raw.TryGetValue("flagged", out var flagged))
        {
            if (flagged != "important")
                errors.Add(new Issue(path, "flagged must be important, or omitted"));
            else
                important = true;
        }

        var normSupersedes = NormalizeIds(path, "supersedes", supersedes, id, errors);
        var normLinks = NormalizeIds(path, "links", links, id, errors);

        if (body.Length == 0)
            errors.Add(new Issue(path, "body is empty"));

        if (errors.Count > 0)
            return false;

        // Visibility is returned even when it is not public so the loader can
        // classify that as a privacy failure rather than a schema typo only.
        note = new NoteDocument
        {
            Id = id,
            Title = title,
            Summary = summary,
            Tags = normTags,
            Source = normSource,
            Authors = normAuthors,
            Created = created,
            Supersedes = normSupersedes,
            Links = normLinks,
            Visibility = visibility,
            Important = important,
            Body = body,
            Path = path
        };
        return true;
    }

    private static string Required(Dictionary<string, string> raw, List<Issue> errors, string path, string key)
    {
        if (raw.TryGetValue(key, out var value) && value.Length > 0)
            return value;
        errors.Add(new Issue(path, "missing " + key));
        return "";
    }

    private static List<string> NormalizeIds(string path, string key, List<string> ids, string self, List<Issue> errors)
    {
        var norm = new List<string>();
        foreach (var id in ids)
        {
            var t = id.Trim();
            if (!NoteIds.IsValid(t))
                errors.Add(new Issue(path, $"{key} id {id} is not a note id"));
            else if (t == self)
                errors.Add(new Issue(path, $"{key} cannot name this note"));
            else if (norm.Contains(t))
                errors.Add(new Issue(path, $"duplicate {key} id {t}"));
            else
                norm.Add(t);
        }
        return norm;
    }

    public static bool IsPublicHttps(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static void ScanFile(string path, string rel, List<Issue> privacy)
    {
        string text;
        try
        {
            text = ReadUtf8(path);
        }
        catch (DecoderFallbackException)
        {
            privacy.Add(new Issue(rel, "not valid UTF-8; refusing to index"));
            return;
        }
        privacy.AddRange(PrivacyGuard.Scan(rel, text));
    }

    private static string ReadUtf8(string path) => Utf8.GetString(File.ReadAllBytes(path));

    private static List<string> SplitList(string value)
    {
        var inner = value[1..^1].Trim();
        if (inner.Length == 0)
            return [];
        return inner.Split(',').Select(s => Unquote(s.Trim())).Where(s => s.Length > 0).ToList();
    }

    private static string Unquote(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return raw[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return raw;
    }

    private static string TrimPreview(string line) =>
        line.Length <= 40 ? line : line[..40];

    private static string? FindCycle(IReadOnlyList<NoteDocument> notes)
    {
        var edges = notes.ToDictionary(n => n.Id, n => n.Supersedes, StringComparer.Ordinal);
        var color = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in edges.Keys)
        {
            if (Visit(id, edges, color))
                return id;
        }
        return null;
    }

    private static bool Visit(string id, Dictionary<string, IReadOnlyList<string>> edges, Dictionary<string, int> color)
    {
        if (!edges.ContainsKey(id))
            return false;
        if (color.TryGetValue(id, out var c))
            return c == 1;
        color[id] = 1;
        foreach (var next in edges[id])
        {
            if (Visit(next, edges, color))
                return true;
        }
        color[id] = 2;
        return false;
    }
}
