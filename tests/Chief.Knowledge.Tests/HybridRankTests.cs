using Microsoft.Data.Sqlite;

namespace Chief.Knowledge.Tests;

public class HybridRankTests
{
    [Fact]
    public void Reciprocal_rank_fusion_promotes_a_note_that_is_good_on_both_lists()
    {
        using var dir = new TempDir();
        // A is the best keyword hit and a poor vector. C is a weaker keyword hit
        // and the closest vector, so fusion ranks C first. B is keywords-free.
        Fixtures.WriteNote(dir.Path, "a-keywords", "alpha alpha alpha alpha alpha", title: "alpha alpha");
        Fixtures.WriteNote(dir.Path, "b-vector-only", "unrelated words here", title: "other");
        Fixtures.WriteNote(dir.Path, "c-both", "alpha", title: "alpha");
        var embedder = Map();
        var index = Build(dir.Path, embedder);
        var hits = KnowledgeIndex.Search(index, "alpha", new SearchRequest(), embedder, Fixtures.Pure()).Hits;
        Assert.Equal(["c-both", "a-keywords", "b-vector-only"], hits.Select(h => h.Id).ToArray());
    }

    [Fact]
    public void Important_and_newer_notes_get_a_small_boost()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "a-plain", "sameword", created: "2026-09-27");
        Fixtures.WriteNote(dir.Path, "b-flagged", "sameword", created: "2026-09-27", important: true);
        var embedder = new ConstEmbedder();
        var index = Build(dir.Path, embedder);
        var flagged = KnowledgeIndex.Search(index, "sameword", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.Equal("b-flagged", flagged.Hits[0].Id);

        using var dates = new TempDir();
        Fixtures.WriteNote(dates.Path, "a-old", "sameword", created: "2020-01-01");
        Fixtures.WriteNote(dates.Path, "b-new", "sameword", created: "2026-09-27");
        var dated = Build(dates.Path, embedder);
        var recent = KnowledgeIndex.Search(dated, "sameword", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.Equal("b-new", recent.Hits[0].Id);
    }

    [Fact]
    public void Every_vector_row_stores_the_model_id_and_a_different_model_is_not_compared()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "visible", "alpha", title: "alpha");
        Fixtures.WriteNote(dir.Path, "hijack", "zzzz", title: "zzzz");
        var embedder = new MapEmbedder("test-map", new Dictionary<string, float[]>
        {
            ["alpha\nalpha\nalpha"] = Norm(0, 1),
            ["zzzz\nzzzz\nzzzz"] = Norm(1, 0),
            ["alpha"] = Norm(1, 0)
        });
        // Title and summary default to "Title" / "Summary." so set them to match the keys above.
        File.WriteAllText(Path.Combine(dir.Path, "visible.md"), Note("visible", "alpha", "alpha"));
        File.WriteAllText(Path.Combine(dir.Path, "hijack.md"), Note("hijack", "zzzz", "zzzz"));
        var index = Build(dir.Path, embedder);

        using (var conn = new SqliteConnection("Data Source=" + index))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, model_id FROM vectors ORDER BY id";
            using var reader = cmd.ExecuteReader();
            var rows = new List<(string Id, string Model)>();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("test-map", r.Model));
            using var flip = conn.CreateCommand();
            flip.CommandText = "UPDATE vectors SET model_id = 'other-model' WHERE id = 'hijack'";
            Assert.Equal(1, flip.ExecuteNonQuery());
        }

        var outcome = KnowledgeIndex.Search(index, "alpha", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.DoesNotContain(outcome.Hits, h => h.Id == "hijack");
        Assert.Equal("visible", outcome.Hits[0].Id);
        Assert.Contains(outcome.Warnings, w => w.Contains("hijack", StringComparison.Ordinal));
    }

    [Fact]
    public void Sqlite_vec_ranks_the_same_note_first_as_the_pure_fallback()
    {
        var so = Path.Combine(Fixtures.RepoRoot(), "src", "Chief.Knowledge", "native", "linux-x64", "vec0.so");
        Assert.True(File.Exists(so));
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "a-keywords", "alpha alpha alpha alpha alpha", title: "alpha alpha");
        Fixtures.WriteNote(dir.Path, "b-vector-only", "unrelated words here", title: "other");
        Fixtures.WriteNote(dir.Path, "c-both", "alpha", title: "alpha");
        var embedder = Map();
        var corpus = NoteLoader.Load(dir.Path);
        Assert.Equal(0, corpus.FailureCode);
        var index = Path.Combine(dir.Path, "hive.sqlite");
        var backend = KnowledgeIndex.Rebuild(index, corpus.Notes, embedder, new IndexOptions
        {
            Backend = VectorBackendPreference.Auto,
            Vec0Path = so,
            Today = new DateOnly(2026, 9, 27)
        });
        var expectedBackend = OperatingSystem.IsLinux() &&
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
            ? "vec0" : "pure";
        Assert.Equal(expectedBackend, backend);
        var vec = KnowledgeIndex.Search(index, "alpha", new SearchRequest(), embedder, new IndexOptions
        {
            Backend = VectorBackendPreference.Auto,
            Vec0Path = so,
            Today = new DateOnly(2026, 9, 27)
        });
        var pure = KnowledgeIndex.Search(index, "alpha", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.Equal(expectedBackend, vec.Backend);
        Assert.Equal("pure", pure.Backend);
        Assert.Equal(pure.Hits.Select(h => h.Id), vec.Hits.Select(h => h.Id));
        Assert.Equal("c-both", vec.Hits[0].Id);
    }

    private static MapEmbedder Map() => new("test-map", new Dictionary<string, float[]>
    {
        ["alpha alpha\nSummary.\nalpha alpha alpha alpha alpha"] = Norm(0, 1),
        ["other\nSummary.\nunrelated words here"] = Norm(0.2f, 0.8f),
        ["alpha\nSummary.\nalpha"] = Norm(1, 0),
        ["alpha"] = Norm(1, 0)
    });

    private static string Build(string dir, IEmbedder embedder)
    {
        var corpus = NoteLoader.Load(dir);
        Assert.True(corpus.FailureCode == 0, string.Join("; ", corpus.Validation.Concat(corpus.Privacy).Select(e => e.Message)));
        var index = Path.Combine(dir, "hive.sqlite");
        KnowledgeIndex.Rebuild(index, corpus.Notes, embedder, Fixtures.Pure());
        return index;
    }

    private static float[] Norm(float x, float y)
    {
        var len = Math.Sqrt(x * x + y * y);
        return [(float)(x / len), (float)(y / len), 0f, 0f];
    }

    private static string Note(string id, string title, string body) =>
        "---\nid: " + id + "\ntitle: " + title + "\nsummary: " + title + "\ntags: [topic]\nsource: alex\nauthors: [chief]\ncreated: 2026-09-27\nvisibility: public\n---\n\n" + body + "\n";
}
