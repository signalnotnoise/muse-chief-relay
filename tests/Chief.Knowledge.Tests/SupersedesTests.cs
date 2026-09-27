namespace Chief.Knowledge.Tests;

public class SupersedesTests
{
    [Fact]
    public void Superseded_notes_are_hidden_by_default_and_demoted_when_included()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "a-old", "alpha term");
        Fixtures.WriteNote(dir.Path, "b-new", "alpha term", supersedes: "a-old");
        var index = Build(dir.Path, out var embedder);

        var hidden = KnowledgeIndex.Search(index, "alpha", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.Equal("b-new", Assert.Single(hidden.Hits).Id);

        var shown = KnowledgeIndex.Search(index, "alpha", new SearchRequest { IncludeSuperseded = true }, embedder, Fixtures.Pure());
        Assert.Equal(["b-new", "a-old"], shown.Hits.Select(h => h.Id).ToArray());
        Assert.True(shown.Hits[1].Superseded);
        Assert.True(shown.Hits[1].Score < shown.Hits[0].Score);
    }

    [Fact]
    public void A_chain_hides_every_note_that_something_supersedes()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "a-old", "alpha term");
        Fixtures.WriteNote(dir.Path, "b-mid", "alpha term", supersedes: "a-old");
        Fixtures.WriteNote(dir.Path, "c-new", "alpha term", supersedes: "b-mid");
        var index = Build(dir.Path, out var embedder);
        var hits = KnowledgeIndex.Search(index, "alpha", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.Equal("c-new", Assert.Single(hits.Hits).Id);
    }

    [Fact]
    public void A_supersedes_cycle_refuses_to_write_an_index()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "a-loop", "alpha", supersedes: "b-loop");
        Fixtures.WriteNote(dir.Path, "b-loop", "alpha", supersedes: "a-loop");
        var index = dir.File("hive.sqlite");
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(
            ["rebuild", "--knowledge", dir.Path, "--index", index],
            new StringWriter(),
            stderr,
            _ => throw new InvalidOperationException("embedder must not run"));
        Assert.Equal(2, code);
        Assert.Contains("cycle", stderr.ToString());
        Assert.False(File.Exists(index));
    }

    private static string Build(string dir, out IEmbedder embedder)
    {
        embedder = new ConstEmbedder();
        var corpus = NoteLoader.Load(dir);
        Assert.Equal(0, corpus.FailureCode);
        var index = Path.Combine(dir, "hive.sqlite");
        KnowledgeIndex.Rebuild(index, corpus.Notes, embedder, Fixtures.Pure());
        return index;
    }
}
