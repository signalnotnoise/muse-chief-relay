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
    public void Lowercase_bearer_and_bare_akia_key_id_fail_closed()
    {
        var lowerBearer = string.Concat("author", "ization: bea", "rer ", "abcdefghijklmnop");
        var bareAkia = string.Concat("AK", "IAIOSFODNN7EXAMPLE");
        var prefixedToken = string.Concat("ghp_", "abcdefghijklmnopqrst");

        AssertKind(lowerBearer, "bearer");
        AssertKind(bareAkia, "API token");
        AssertKind(prefixedToken, "API token");
        Assert.DoesNotContain(PrivacyGuard.Scan("planted.md", bareAkia), f => f.Message.Contains("EXAMPLE"));
        // Case still matters for the AKIA alternative; prose must not trip it.
        Assert.Empty(PrivacyGuard.Scan("prose.md", "The akia was a typo for akira."));

        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "leaked", "The key id " + bareAkia + " must not ship.");
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(["check", "--knowledge", dir.Path], new StringWriter(), stderr);
        Assert.Equal(1, code);
        var report = stderr.ToString();
        Assert.Contains("refused", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API token", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", report);
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
    public void Quoted_multi_word_assignment_with_a_short_first_token_fails_closed()
    {
        // The first word is under the minimum; the quoted span is not.
        var shortFirst = string.Concat("\"pass", "word\": \"", "my secret\"");
        var longFirst = string.Concat("\"pass", "word\": \"", "secret phrase\"");
        var singleQuoted = string.Concat("pass", "word: '", "my secret", "'");
        var token = string.Concat("\"api", "_key\": \"", "my secret\"");
        var trip = string.Concat("\"trip_", "password\": \"", "my secret\"");

        AssertKind(longFirst, "password");
        AssertKind(shortFirst, "password");
        AssertKind(singleQuoted, "password");
        AssertKind(token, "token");
        AssertKind(trip, "trip password");

        var shortFound = PrivacyGuard.Scan("planted.md", shortFirst);
        Assert.DoesNotContain(shortFound, f => f.Message.Contains("my secret"));

        // The minimum still applies to the whole quoted span, and to an unquoted token.
        Assert.Empty(PrivacyGuard.Scan("prose.md", string.Concat("\"pass", "word\": \"", "ab\"")));
        Assert.Empty(PrivacyGuard.Scan("prose.md", string.Concat("pass", "word: ", "ab")));
        Assert.Empty(PrivacyGuard.Scan("prose.md", string.Concat("\"to", "ken\": \"", "short\"")));

        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "leaked", shortFirst);
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(["check", "--knowledge", dir.Path], new StringWriter(), stderr);
        Assert.Equal(1, code);
        var report = stderr.ToString();
        Assert.Contains("password", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("my secret", report);
    }

    [Fact]
    public void A_secret_in_the_filename_or_path_fails_closed_when_the_body_is_clean()
    {
        const string clean = "This note body has nothing to hide.";
        var keyId = string.Concat("AK", "IAIOSFODNN7EXAMPLE");
        var prefixed = string.Concat("ghp_", "abcdefghijklmnopqrst");
        var assigned = string.Concat("pass", "word=", "notreal1") + ".md";

        var byName = PrivacyGuard.Scan(keyId + ".md", clean);
        Assert.NotEmpty(byName);
        Assert.Contains(byName, f => f.Message.StartsWith("path:", StringComparison.Ordinal) && f.Message.Contains("API token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(byName, f => f.Message.Contains("EXAMPLE"));

        var bySegment = PrivacyGuard.Scan("nested/" + prefixed + "/note.md", clean);
        Assert.Contains(bySegment, f => f.Message.StartsWith("path:", StringComparison.Ordinal) && f.Message.Contains("API token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(bySegment, f => f.Message.Contains(prefixed));

        var bySeparator = PrivacyGuard.Scan("nested\\" + prefixed + "\\note.md", clean);
        Assert.Contains(bySeparator, f => f.Message.StartsWith("path:", StringComparison.Ordinal) && f.Message.Contains("API token", StringComparison.OrdinalIgnoreCase));

        var byAssignment = PrivacyGuard.Scan(assigned, clean);
        Assert.Contains(byAssignment, f => f.Message.StartsWith("path:", StringComparison.Ordinal) && f.Message.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(byAssignment, f => f.Message.Contains("notreal1"));

        Assert.Empty(PrivacyGuard.Scan("my-password-notes.md", clean));
        Assert.Empty(PrivacyGuard.Scan("clean-note.md", clean));

        var rawName = "room-channel";
        var raw = PrivacyGuard.Scan("boards/" + rawName + ".jsonl", clean);
        Assert.Contains(raw, f => f.Message.StartsWith("path:", StringComparison.Ordinal) && f.Message.Contains("raw channel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(raw, f => f.Message.Contains(rawName));

        var hash = new string('a', 64);
        Assert.Empty(PrivacyGuard.Scan("boards/" + hash + ".jsonl", clean));
        Assert.Empty(PrivacyGuard.Scan("boards/<sha256(trimmed channel)>.jsonl", clean));

        using var dir = new TempDir();
        File.WriteAllText(dir.File(keyId + ".md"), """
            ---
            id: clean-note
            title: Clean
            summary: Nothing planted in the body.
            tags: [privacy]
            source: alex
            authors: [chief]
            created: 2026-09-28
            visibility: public
            ---

            This note body has nothing to hide.
            """);
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(["check", "--knowledge", dir.Path], new StringWriter(), stderr);
        Assert.Equal(1, code);
        var report = stderr.ToString();
        Assert.Contains("path:", report, StringComparison.Ordinal);
        Assert.Contains("API token", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refused", report, StringComparison.OrdinalIgnoreCase);
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
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(report, "not public", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
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
    public void A_raw_board_path_fails_closed_and_a_hash_or_placeholder_does_not()
    {
        var rawName = "room-channel";
        var raw = "boards/" + rawName + ".jsonl";
        var found = PrivacyGuard.Scan("planted.md", "This room's file is " + raw + ".");
        Assert.NotEmpty(found);
        Assert.Contains(found, f => f.Message.Contains("raw channel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.Message.Contains(rawName));

        var loose = PrivacyGuard.Scan("planted.md", "boards/<room>.jsonl");
        Assert.NotEmpty(loose);
        Assert.DoesNotContain(loose, f => f.Message.Contains("<room>"));

        Assert.NotEmpty(PrivacyGuard.Scan("planted.md", "boards/" + new string('A', 64) + ".jsonl"));
        Assert.NotEmpty(PrivacyGuard.Scan("planted.md", "boards/" + new string('a', 63) + ".jsonl"));
        Assert.NotEmpty(PrivacyGuard.Scan("planted.md", "boards/" + new string('a', 65) + ".jsonl"));

        var hash = new string('a', 64);
        Assert.Empty(PrivacyGuard.Scan("ok.md", "See boards/" + hash + ".jsonl for it."));
        Assert.Empty(PrivacyGuard.Scan("ok.md", "The file is `boards/<sha256(trimmed channel)>.jsonl`."));

        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "leaked", "See " + raw + ".");
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(["check", "--knowledge", dir.Path], new StringWriter(), stderr);
        Assert.Equal(1, code);
        var report = stderr.ToString();
        Assert.Contains("raw channel", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawName, report);
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
