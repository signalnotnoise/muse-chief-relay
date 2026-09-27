namespace Chief.Knowledge.Tests;

public class PrivacyGuardTests
{
    [Fact]
    public void Prose_about_passwords_is_not_a_secret()
    {
        const string prose = """
            ---
            id: masked
            title: The password field is masked
            summary: The trip password is never logged and a session token is redacted.
            tags: [privacy]
            source: alex
            authors: [chief]
            created: 2026-09-27
            visibility: public
            ---

            The password field is masked. The trip password is never logged. A session token is redacted.
            """;
        Assert.Empty(PrivacyGuard.Scan("masked.md", prose));
    }

    [Fact]
    public void A_planted_bearer_token_password_and_trip_secret_fail_closed()
    {
        var bearer = string.Concat("Author", "ization: Bea", "rer ", "abcdefghijklmnop");
        var password = string.Concat("pass", "word: ", "notreal1");
        var trip = string.Concat("trip-", "password: ", "notreal1");
        var nick = string.Concat("alex", "#", "notreal");
        var url = string.Concat("https://", "user:notreal1@", "example.com/hook");

        AssertKind(bearer, "bearer");
        AssertKind(password, "password");
        AssertKind(trip, "trip password");
        AssertKind(nick, "nick#password");
        AssertKind(url, "credentials");

        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "kept", "kepttoken stays in the old index.");
        var index = dir.File("hive.sqlite");
        var embedder = new ConstEmbedder();
        var corpus = NoteLoader.Load(dir.Path);
        Assert.Equal(0, corpus.FailureCode);
        KnowledgeIndex.Rebuild(index, corpus.Notes, embedder, Fixtures.Pure());

        File.WriteAllText(dir.File("leaked.md"), "---\nid: leaked\ntitle: Leaked\nsummary: secretoken should not be indexed.\ntags: [topic]\nsource: alex\nauthors: [chief]\ncreated: 2026-09-27\nvisibility: public\n---\n\n" + bearer + "\n");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(["rebuild", "--knowledge", dir.Path, "--index", index, "--backend", "pure"], stdout, stderr, _ => throw new InvalidOperationException("embedder must not run"));
        Assert.Equal(1, code);
        Assert.DoesNotContain("abcdefghijklmnop", stderr.ToString());
        Assert.Contains("refused", stderr.ToString());
        Assert.DoesNotContain("hive-building", string.Join('\n', Directory.EnumerateFiles(dir.Path)));

        var outcome = KnowledgeIndex.Search(index, "kepttoken", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.Equal("kept", Assert.Single(outcome.Hits).Id);
        var leaked = KnowledgeIndex.Search(index, "secretoken", new SearchRequest(), embedder, Fixtures.Pure());
        Assert.DoesNotContain(leaked.Hits, h => h.Id == "leaked");
    }

    [Fact]
    public void Json_form_and_prefixed_names_fail_closed()
    {
        var jsonPassword = string.Concat("\"pass", "word\": \"", "hunter2supersecret\"");
        var jsonApiKey = string.Concat("\"api", "_key\": \"", "abcdefghijklmnop\"");
        var prefixed = string.Concat("my_pass", "word = ", "notreal1");
        var jsonTrip = string.Concat("\"trip_", "password\": \"", "notreal1\"");

        AssertKind(jsonPassword, "password");
        AssertKind(jsonApiKey, "token");
        AssertKind(prefixed, "password");
        AssertKind(jsonTrip, "trip password");
        Assert.DoesNotContain(PrivacyGuard.Scan("planted.md", jsonPassword), f => f.Message.Contains("hunter2"));

        Assert.Empty(PrivacyGuard.Scan("prose.md", "bypass = northward"));
        Assert.Empty(PrivacyGuard.Scan("prose.md", "compass: north"));

        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "leaked", jsonPassword);
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(["check", "--knowledge", dir.Path], new StringWriter(), stderr);
        Assert.Equal(1, code);
        Assert.Contains("password", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", stderr.ToString());
    }

    [Fact]
    public void A_private_note_refuses_the_index_and_check_writes_nothing()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "hidden", "This must not be indexed.", visibility: "private");
        var index = dir.File("hive.sqlite");
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(["check", "--knowledge", dir.Path], new StringWriter(), stderr, _ => throw new InvalidOperationException());
        Assert.Equal(1, code);
        Assert.False(File.Exists(index));
        Assert.False(Directory.Exists(dir.File(".index")));

        stderr = new StringWriter();
        code = KnowledgeCli.Run(["rebuild", "--knowledge", dir.Path, "--index", index], new StringWriter(), stderr, _ => throw new InvalidOperationException("embedder must not run"));
        Assert.Equal(1, code);
        Assert.False(File.Exists(index));
        var report = stderr.ToString();
        Assert.Contains("not public", report, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(report, "not public", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
    }

    [Fact]
    public void Add_refuses_a_secret_and_does_not_create_the_file()
    {
        using var dir = new TempDir();
        var secret = string.Concat("pass", "word: ", "notreal1");
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(
            ["add", "--knowledge", dir.Path, "--title", "Nope", "--summary", "Do not store this.", "--tag", "topic", "--source", "alex", "--author", "chief", "--body", secret],
            new StringWriter(), stderr);
        Assert.Equal(1, code);
        Assert.Empty(Directory.EnumerateFiles(dir.Path));
        Assert.DoesNotContain("notreal1", stderr.ToString());
    }

    [Fact]
    public void Shipped_notes_pass_the_privacy_check()
    {
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(
            ["check", "--knowledge", Path.Combine(Fixtures.RepoRoot(), "knowledge")],
            new StringWriter(),
            stderr);
        Assert.True(code == 0, stderr.ToString());
    }

    private static void AssertKind(string text, string kind)
    {
        var found = PrivacyGuard.Scan("planted.md", text);
        Assert.NotEmpty(found);
        Assert.Contains(found, f => f.Message.Contains(kind, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.Message.Contains("notreal") || f.Message.Contains("abcdefghijklmnop"));
    }
}
