namespace Chief.Knowledge.Tests;

public class EmbedderTests
{
    [Fact]
    public void WordPiece_matches_the_MiniLM_vocab_on_known_sentences()
    {
        var vocab = Path.Combine(Fixtures.RepoRoot(), "src", "Chief.Knowledge", "models", "vocab.txt");
        var tokenizer = new BertWordPieceTokenizer(vocab);
        Assert.Equal(new[] { 101, 1996, 2958, 7906, 28667, 18256, 11873, 102 }, tokenizer.Encode("The bridge keeps reconnecting"));
        Assert.Equal(new[] { 101, 10975, 1001, 2321, 28667, 18256, 6593, 1012, 1046, 2015, 102 }, tokenizer.Encode("PR #15 reconnect.js"));
        Assert.Equal(new[] { 101, 7668, 102 }, tokenizer.Encode("café"));
    }

    [Fact]
    public void MiniLM_ranks_a_related_sentence_closer_than_an_unrelated_one()
    {
        var vocab = Path.Combine(Fixtures.RepoRoot(), "src", "Chief.Knowledge", "models", "vocab.txt");
        var cache = Path.Combine(Path.GetTempPath(), "chief-knowledge-minilm");
        using var embedder = OnnxMiniLmEmbedder.Create(vocab, cache);
        Assert.Equal(MiniLm.ModelId, embedder.ModelId);
        var dropped = embedder.Embed("The bridge keeps reconnecting after a dropped session");
        var retry = embedder.Embed("Chief.Bridge retries forever when the socket drops");
        var bread = embedder.Embed("banana bread recipe with walnuts");
        var related = VectorMath.Cosine(dropped, retry);
        var unrelated = VectorMath.Cosine(dropped, bread);
        Assert.True(related > 0.55, "related cosine was " + related);
        Assert.True(unrelated < 0.2, "unrelated cosine was " + unrelated);
        Assert.True(related > unrelated);
    }
}
