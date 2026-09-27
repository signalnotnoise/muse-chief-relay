namespace Chief.Knowledge.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("chief-knowledge-tests-").FullName;
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class Fixtures
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "MuseChiefRelay.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found");
    }

    public static void WriteNote(
        string dir,
        string id,
        string body,
        string title = "Title",
        string summary = "Summary.",
        string tags = "topic",
        string authors = "chief",
        string created = "2026-09-27",
        string source = "alex",
        string supersedes = "",
        string links = "",
        bool important = false,
        string visibility = "public")
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("---\n");
        sb.Append("id: ").Append(id).Append('\n');
        sb.Append("title: ").Append(title).Append('\n');
        sb.Append("summary: ").Append(summary).Append('\n');
        sb.Append("tags: [").Append(tags).Append("]\n");
        sb.Append("source: ").Append(source).Append('\n');
        sb.Append("authors: [").Append(authors).Append("]\n");
        sb.Append("created: ").Append(created).Append('\n');
        if (supersedes.Length > 0)
            sb.Append("supersedes: [").Append(supersedes).Append("]\n");
        if (links.Length > 0)
            sb.Append("links: [").Append(links).Append("]\n");
        sb.Append("visibility: ").Append(visibility).Append('\n');
        if (important)
            sb.Append("flagged: important\n");
        sb.Append("---\n\n");
        sb.Append(body.TrimEnd()).Append('\n');
        File.WriteAllText(System.IO.Path.Combine(dir, id + ".md"), sb.ToString());
    }

    public static IndexOptions Pure(DateOnly? today = null) => new()
    {
        Backend = VectorBackendPreference.Pure,
        Today = today ?? new DateOnly(2026, 9, 27)
    };

    public static SearchHit First(string index, string query, IEmbedder embedder, SearchRequest? request = null, IndexOptions? options = null)
    {
        var outcome = KnowledgeIndex.Search(index, query, request ?? new SearchRequest(), embedder, options ?? Pure());
        Assert.NotEmpty(outcome.Hits);
        return outcome.Hits[0];
    }
}

internal sealed class ConstEmbedder : IEmbedder
{
    public ConstEmbedder(string modelId = "test-const") => ModelId = modelId;
    public string ModelId { get; }
    public int Dimensions => 4;
    public float[] Embed(string text) => [1f, 0f, 0f, 0f];
}

internal sealed class MapEmbedder : IEmbedder
{
    private readonly Dictionary<string, float[]> _map;
    public MapEmbedder(string modelId, Dictionary<string, float[]> map)
    {
        ModelId = modelId;
        _map = map;
    }

    public string ModelId { get; }
    public int Dimensions => 4;

    public float[] Embed(string text)
    {
        if (!_map.TryGetValue(text, out var vector))
            throw new InvalidOperationException("no vector for [" + text + "]");
        return vector;
    }
}
