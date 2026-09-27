using System.Globalization;
using System.Text.Json;

namespace Chief.Knowledge;

public static class KnowledgeCli
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string, IEmbedder>? embedderFactory = null)
    {
        try
        {
            return RunCore(args, stdout, stderr, embedderFactory);
        }
        catch (KnowledgeException ex)
        {
            stderr.WriteLine("[knowledge] " + ex.Message);
            return ex.ExitCode;
        }
    }

    private static int RunCore(string[] args, TextWriter stdout, TextWriter stderr, Func<string, IEmbedder>? embedderFactory)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            stdout.WriteLine(Help);
            return 0;
        }

        var command = args[0];
        var rest = args.Skip(1).ToArray();
        return command switch
        {
            "check" => CmdCheck(Parse(rest), stdout, stderr),
            "rebuild" => CmdRebuild(Parse(rest), stdout, stderr, embedderFactory),
            "search" => CmdSearch(Parse(rest), stdout, stderr, embedderFactory),
            "add" => CmdAdd(Parse(rest), stdout, stderr),
            _ => throw new KnowledgeException(2, "unknown command " + command + "\n" + Help)
        };
    }

    private static int CmdCheck(Parsed args, TextWriter stdout, TextWriter stderr)
    {
        args.RejectUnknown("knowledge");
        if (args.Positionals.Count > 0)
            throw new KnowledgeException(2, "unexpected argument " + args.Positionals[0]);
        var dir = KnowledgeDir(args);
        var corpus = NoteLoader.Load(dir);
        var code = Report(corpus, stderr);
        if (code == 0)
            stdout.WriteLine("ok: " + corpus.Notes.Count + (corpus.Notes.Count == 1 ? " note" : " notes"));
        return code;
    }

    private static int CmdRebuild(Parsed args, TextWriter stdout, TextWriter stderr, Func<string, IEmbedder>? embedderFactory)
    {
        args.RejectUnknown("knowledge", "index", "backend", "vec0", "today");
        if (args.Positionals.Count > 0)
            throw new KnowledgeException(2, "unexpected argument " + args.Positionals[0]);
        var dir = KnowledgeDir(args);
        var corpus = NoteLoader.Load(dir);
        var code = Report(corpus, stderr);
        if (code != 0)
            return code;

        var index = IndexPath(args, dir);
        using var embedder = Disposable(embedderFactory?.Invoke(dir) ?? EmbedderFactory.CreateDefault(dir));
        var options = Options(args, embedder.Value);
        var backend = KnowledgeIndex.Rebuild(index, corpus.Notes, embedder.Value, options);
        stderr.WriteLine($"[knowledge] indexed {corpus.Notes.Count} notes with {embedder.Value.ModelId} ({backend})");
        stdout.WriteLine(index);
        return 0;
    }

    private static int CmdSearch(Parsed args, TextWriter stdout, TextWriter stderr, Func<string, IEmbedder>? embedderFactory)
    {
        args.RejectUnknown("knowledge", "index", "backend", "vec0", "today", "tag", "author", "since", "until", "source", "include-superseded", "json", "limit");
        if (args.Positionals.Count == 0)
            throw new KnowledgeException(2, "search needs a query");
        var query = string.Join(' ', args.Positionals);
        var dir = KnowledgeDir(args);
        var index = IndexPath(args, dir);
        using var embedder = Disposable(embedderFactory?.Invoke(dir) ?? EmbedderFactory.CreateDefault(dir));
        var request = new SearchRequest
        {
            Tags = args.Many("tag").Select(t => t.ToLowerInvariant()).ToList(),
            Authors = args.Many("author").Select(a => a.ToLowerInvariant()).ToList(),
            Since = args.Date("since"),
            Until = args.Date("until"),
            Source = args.One("source"),
            IncludeSuperseded = args.Has("include-superseded"),
            Limit = args.Int("limit", 10)
        };
        var outcome = KnowledgeIndex.Search(index, query, request, embedder.Value, Options(args, embedder.Value));
        foreach (var warning in outcome.Warnings)
            stderr.WriteLine("[knowledge] warning: " + warning);
        if (args.Has("json"))
        {
            stdout.WriteLine(JsonSerializer.Serialize(outcome.Hits, JsonOpts));
            return 0;
        }
        if (outcome.Hits.Count == 0)
        {
            stdout.WriteLine("(no matches)");
            return 0;
        }
        foreach (var hit in outcome.Hits)
        {
            var flags = hit.Important ? " important" : "";
            var sup = hit.Superseded ? " superseded" : "";
            stdout.WriteLine($"{hit.Id}  score={hit.Score.ToString("0.0000", CultureInfo.InvariantCulture)}  {hit.Created}{flags}{sup}  {string.Join(",", hit.Authors)}  [{string.Join(", ", hit.Tags)}]");
            stdout.WriteLine("  " + hit.Summary);
        }
        return 0;
    }

    private static int CmdAdd(Parsed args, TextWriter stdout, TextWriter stderr)
    {
        args.RejectUnknown("knowledge", "title", "summary", "tag", "source", "author", "id", "created", "supersedes", "link", "flagged", "body", "body-file");
        if (args.Positionals.Count > 0)
            throw new KnowledgeException(2, "unexpected argument " + args.Positionals[0]);
        var dir = KnowledgeDir(args);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var existing = NoteLoader.Load(dir);
        var code = Report(existing, stderr);
        if (code != 0)
            return code;

        var title = args.Required("title");
        var summary = args.Required("summary");
        var source = args.Required("source");
        var authors = args.Many("author");
        if (authors.Count == 0)
            throw new KnowledgeException(2, "add needs --author chief|fuse|alex");
        var tags = args.Many("tag").SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (tags.Count == 0)
            throw new KnowledgeException(2, "add needs at least one --tag");

        string body;
        var bodyArg = args.One("body");
        var bodyFile = args.One("body-file");
        if (bodyArg is not null && bodyFile is not null)
            throw new KnowledgeException(2, "pass --body or --body-file, not both");
        if (bodyFile is not null)
            body = File.ReadAllText(bodyFile);
        else if (bodyArg is not null)
            body = bodyArg;
        else
            body = Console.In.ReadToEnd();

        var id = args.One("id") ?? NoteIds.Slug(title);
        if (id.Length == 0)
            throw new KnowledgeException(2, "could not make an id from the title; pass --id");
        var created = args.Date("created") ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var flagged = args.One("flagged");

        var draft = new NoteDocument
        {
            Id = id,
            Title = title,
            Summary = summary,
            Tags = tags,
            Source = source,
            Authors = authors,
            Created = created,
            Supersedes = args.Many("supersedes"),
            Links = args.Many("link"),
            Visibility = "public",
            Important = flagged is not null,
            Body = body.Trim(),
            Path = id + ".md"
        };
        if (flagged is not null && flagged != "important")
            throw new KnowledgeException(2, "--flagged must be important");

        var rendered = NoteWriter.Render(draft);
        // Parse the rendered note so add and rebuild share one validator, then
        // write the canonical form (authors and tags lowercased).
        if (!NoteLoader.TryParse(draft.Path, rendered, out var note, out var errors))
        {
            foreach (var error in errors)
                stderr.WriteLine("[knowledge] " + error.Path + ": " + error.Message);
            return 2;
        }
        var canonical = NoteWriter.Render(note);
        var secrets = PrivacyGuard.Scan(draft.Path, canonical);
        if (secrets.Count > 0 || note.Visibility != "public")
        {
            foreach (var secret in secrets)
                stderr.WriteLine("[knowledge] " + secret.Path + ": " + secret.Message);
            stderr.WriteLine("[knowledge] refused to write the note");
            return 1;
        }

        var dest = Path.Combine(dir, note.Id + ".md");
        if (File.Exists(dest))
            throw new KnowledgeException(2, dest + " already exists");
        File.WriteAllText(dest, canonical);
        stdout.WriteLine(dest);
        stderr.WriteLine("[knowledge] wrote " + note.Id + ".md; run rebuild before search");
        return 0;
    }

    private static int Report(Corpus corpus, TextWriter stderr)
    {
        foreach (var issue in corpus.Privacy)
            stderr.WriteLine("[knowledge] " + issue.Path + ": " + issue.Message);
        foreach (var issue in corpus.Validation)
            stderr.WriteLine("[knowledge] " + issue.Path + ": " + issue.Message);
        if (corpus.FailureCode != 0)
            stderr.WriteLine("[knowledge] refused; no index written");
        else
        {
            foreach (var warning in corpus.Warnings)
                stderr.WriteLine("[knowledge] warning: " + warning.Path + ": " + warning.Message);
        }
        return corpus.FailureCode;
    }

    private static string KnowledgeDir(Parsed args)
    {
        var given = args.One("knowledge");
        if (!string.IsNullOrEmpty(given))
            return Path.GetFullPath(given);
        var root = FindRepo(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "knowledge");
    }

    private static string IndexPath(Parsed args, string knowledgeDir)
    {
        var given = args.One("index");
        if (!string.IsNullOrEmpty(given))
            return Path.GetFullPath(given);
        return Path.Combine(knowledgeDir, ".index", "hive.sqlite");
    }

    private static IndexOptions Options(Parsed args, IEmbedder embedder)
    {
        _ = embedder;
        var backend = string.Equals(args.One("backend"), "pure", StringComparison.OrdinalIgnoreCase)
            ? VectorBackendPreference.Pure
            : VectorBackendPreference.Auto;
        return new IndexOptions
        {
            Backend = backend,
            Vec0Path = args.One("vec0"),
            Today = args.Date("today") ?? DateOnly.FromDateTime(DateTime.UtcNow)
        };
    }

    public static string? FindRepo(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseChiefRelay.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static Holder Disposable(IEmbedder embedder) => new(embedder);

    private sealed class Holder : IDisposable
    {
        public Holder(IEmbedder value) => Value = value;
        public IEmbedder Value { get; }
        public void Dispose() => (Value as IDisposable)?.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static Parsed Parse(string[] args)
    {
        var switches = new HashSet<string>(StringComparer.Ordinal) { "json", "include-superseded" };
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                positionals.AddRange(args[(i + 1)..]);
                break;
            }
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                positionals.Add(arg);
                continue;
            }
            var name = arg[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            if (switches.Contains(name))
            {
                if (value is not null)
                    throw new KnowledgeException(2, "--" + name + " does not take a value");
                Add(map, name, "true");
                continue;
            }
            if (value is null)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new KnowledgeException(2, "missing value for --" + name);
                value = args[++i];
            }
            Add(map, name, value);
        }
        return new Parsed(map, positionals);
    }

    private static void Add(Dictionary<string, List<string>> map, string name, string value)
    {
        if (!map.TryGetValue(name, out var list))
        {
            list = new List<string>();
            map[name] = list;
        }
        list.Add(value);
    }

    private sealed class Parsed
    {
        private readonly Dictionary<string, List<string>> _map;
        public List<string> Positionals { get; }

        public Parsed(Dictionary<string, List<string>> map, List<string> positionals)
        {
            _map = map;
            Positionals = positionals;
        }

        public bool Has(string name) => _map.ContainsKey(name);

        public string? One(string name)
        {
            if (!_map.TryGetValue(name, out var list))
                return null;
            if (list.Count > 1)
                throw new KnowledgeException(2, "--" + name + " was given more than once");
            return list[0];
        }

        public string Required(string name) =>
            One(name) ?? throw new KnowledgeException(2, "missing --" + name);

        public IReadOnlyList<string> Many(string name) =>
            _map.TryGetValue(name, out var list) ? list : [];

        public DateOnly? Date(string name)
        {
            var raw = One(name);
            if (raw is null)
                return null;
            if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new KnowledgeException(2, "--" + name + " must be YYYY-MM-DD");
            return date;
        }

        public void RejectUnknown(params string[] allowed)
        {
            var ok = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var key in _map.Keys)
            {
                if (!ok.Contains(key))
                    throw new KnowledgeException(2, "unknown option --" + key);
            }
        }

        public int Int(string name, int fallback)
        {
            var raw = One(name);
            if (raw is null)
                return fallback;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                throw new KnowledgeException(2, "--" + name + " must be a positive integer");
            return n;
        }
    }

    private const string Help = """
        chief-knowledge — markdown notes under knowledge/, SQLite index beside them

        check                         scan knowledge/ and exit 1 on private notes or secrets
        rebuild                       fail closed, then rebuild knowledge/.index/hive.sqlite
        search <query> [--tag T] [--author A] [--since YYYY-MM-DD] [--until YYYY-MM-DD] [--source S]
        add --title T --summary S --tag T --source alex|room|https://... --author chief|fuse|alex
            [--id ID] [--created YYYY-MM-DD] [--supersedes ID] [--link ID] [--flagged important]
            [--body TEXT | --body-file PATH]

        search also takes --include-superseded, --json, and --limit N.
        Exit 0 is success, 1 is a privacy failure (no index written), 2 is usage or a bad note.

        The default embedder is a local MiniLM ONNX model (no API key). It is downloaded
        once into knowledge/.index/models/ and checked by hash. CHIEF_KNOWLEDGE_EMBEDDER=hash
        selects an offline hashing embedder that does not rank by meaning.
        Sensitive notes stay in a store outside this repo. Never put one under knowledge/.
        """;
}
