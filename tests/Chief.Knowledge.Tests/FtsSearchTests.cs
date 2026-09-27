namespace Chief.Knowledge.Tests;

public class FtsSearchTests
{
    [Theory]
    [InlineData("reconnect.js", "\"reconnect js\"")]
    [InlineData("#15", "\"15\"")]
    [InlineData("PR #15", "\"pr\" AND \"15\"")]
    public void Exact_terms_become_an_fts_phrase(string query, string match) =>
        Assert.Equal(match, Ranking.FtsMatch(query));

    [Fact]
    public void Fts_hits_a_pr_number_and_a_filename_ahead_of_a_near_miss()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "reconnect-forever", "The fix is in reconnect.js and landed as PR #15.", title: "Reconnect forever", tags: "bridge, reconnect");
        Fixtures.WriteNote(dir.Path, "other", "We should reconnect the tests later.", title: "Later", tags: "bridge");
        var index = Build(dir.Path, out var embedder);

        var file = KnowledgeIndex.Search(index, "reconnect.js", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.Equal("reconnect-forever", file.Hits[0].Id);
        Assert.True(file.Hits[0].Score > file.Hits.Single(h => h.Id == "other").Score);

        var pr = KnowledgeIndex.Search(index, "15", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.Equal("reconnect-forever", pr.Hits[0].Id);
    }

    [Fact]
    public void Tag_author_and_date_filters_apply_before_ranking()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "decoy", string.Join(' ', Enumerable.Repeat("alpha", 12)), title: "alpha", tags: "other", authors: "fuse", created: "2020-01-01");
        Fixtures.WriteNote(dir.Path, "kept", "alpha once", title: "alpha", tags: "kept", authors: "chief", created: "2026-09-27");
        var index = Build(dir.Path, out var embedder);
        var options = Fixtures.Pure();
        var request = new SearchRequest { Tags = ["kept"], Authors = ["chief"], Since = new DateOnly(2026, 6, 1), Source = "alex" };
        var outcome = KnowledgeIndex.Search(index, "alpha", request, embedder, options);
        var hit = Assert.Single(outcome.Hits);
        Assert.Equal("kept", hit.Id);
        var expected = Ranking.Fuse(1, 1, false, 0, false);
        Assert.Equal(expected, hit.Score, precision: 9);
    }

    private static string Build(string dir, out IEmbedder embedder)
    {
        embedder = new ConstEmbedder();
        var corpus = NoteLoader.Load(dir);
        Assert.Equal(0, corpus.FailureCode);
        var index = Path.Combine(dir, ".index", "hive.sqlite");
        KnowledgeIndex.Rebuild(index, corpus.Notes, embedder, Fixtures.Pure());
        return index;
    }
}
