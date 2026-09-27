namespace Chief.Knowledge.Tests;

public class FrontMatterTests
{
    [Fact]
    public void A_valid_note_round_trips_through_the_writer()
    {
        const string raw = """
            ---
            id: reconnect-forever
            title: The bridge keeps reconnecting after a drop
            summary: "#15 closed the holes that could still end a retry."
            tags: [bridge, reconnect]
            source: https://github.com/signalnotnoise/muse-chief-relay/pull/15
            authors: [chief, fuse]
            created: 2026-09-27
            supersedes: [old-note]
            links: [csharp-bridge-replaces-python]
            visibility: public
            flagged: important
            ---

            The decision lives in reconnect.js.
            """;
        Assert.True(NoteLoader.TryParse("reconnect-forever.md", raw, out var note, out var errors), string.Join("; ", errors.Select(e => e.Message)));
        Assert.Equal("reconnect-forever", note.Id);
        Assert.Equal(new DateOnly(2026, 9, 27), note.Created);
        Assert.Equal(["bridge", "reconnect"], note.Tags);
        Assert.Equal(["chief", "fuse"], note.Authors);
        Assert.Equal(["old-note"], note.Supersedes);
        Assert.True(note.Important);
        Assert.Contains("reconnect.js", note.Body);

        Assert.True(NoteLoader.TryParse("reconnect-forever.md", NoteWriter.Render(note), out var again, out var againErrors), string.Join("; ", againErrors.Select(e => e.Message)));
        Assert.Equal(note.Summary, again.Summary);
        Assert.Equal(note.Body, again.Body);
        Assert.Equal(note.Supersedes, again.Supersedes);
        Assert.True(again.Important);
    }

    [Fact]
    public void Block_lists_and_a_lowercased_author_are_accepted()
    {
        const string raw = """
            ---
            id: room-note
            title: Room note
            summary: One line.
            tags:
              - boards
              - tasks
            source: Room
            authors:
              - Fuse
            created: 2026-09-27
            visibility: public
            ---

            Body.
            """;
        Assert.True(NoteLoader.TryParse("room-note.md", raw, out var note, out var errors), string.Join("; ", errors.Select(e => e.Message)));
        Assert.Equal(["boards", "tasks"], note.Tags);
        Assert.Equal(["fuse"], note.Authors);
        Assert.Equal("room", note.Source);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("authors")]
    [InlineData("created")]
    public void Missing_required_fields_are_rejected(string drop)
    {
        var raw = """
            ---
            id: n
            title: Title
            summary: One line.
            tags: [topic]
            source: alex
            authors: [chief]
            created: 2026-09-27
            visibility: public
            ---

            Body.
            """;
        raw = raw.Replace(drop + ":", drop + "-gone:", StringComparison.Ordinal);
        Assert.False(NoteLoader.TryParse("n.md", raw, out _, out var errors));
        Assert.Contains(errors, e => e.Message.Contains(drop, StringComparison.Ordinal) || e.Message.Contains("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void A_summary_longer_than_one_line_and_bad_fields_are_rejected()
    {
        Assert.False(NoteLoader.TryParse("n.md", Note(summary: new string('x', 241)), out _, out var longSummary));
        Assert.Contains(longSummary, e => e.Message.Contains("240"));

        Assert.False(NoteLoader.TryParse("n.md", Note(authors: "muse"), out _, out var author));
        Assert.Contains(author, e => e.Message.Contains("author"));

        Assert.False(NoteLoader.TryParse("n.md", Note(created: "27-09-2026"), out _, out var date));
        Assert.Contains(date, e => e.Message.Contains("YYYY-MM-DD"));

        Assert.False(NoteLoader.TryParse("n.md", Note(source: "secret-channel"), out _, out var source));
        Assert.Contains(source, e => e.Message.Contains("channel"));

        Assert.False(NoteLoader.TryParse("n.md", Note(supersedes: "n"), out _, out var self));
        Assert.Contains(self, e => e.Message.Contains("cannot name this note"));

        Assert.False(NoteLoader.TryParse("n.md", Note(body: "  \n"), out _, out var body));
        Assert.Contains(body, e => e.Message.Contains("body"));
    }

    [Fact]
    public void The_file_name_must_match_the_id_and_add_fills_the_front_matter()
    {
        using var dir = new TempDir();
        Fixtures.WriteNote(dir.Path, "real-id", "Body.");
        File.Move(dir.File("real-id.md"), dir.File("other.md"));
        var corpus = NoteLoader.Load(dir.Path);
        Assert.Contains(corpus.Validation, e => e.Message.Contains("file name"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = KnowledgeCli.Run(
            ["add", "--knowledge", dir.File("fresh"), "--title", "Remember this", "--summary", "Alex said remember this.", "--tag", "Room", "--source", "alex", "--author", "Chief", "--body", "The room decided."],
            stdout, stderr);
        Assert.Equal(0, code);
        var written = File.ReadAllText(dir.File("fresh/remember-this.md"));
        Assert.Contains("id: remember-this", written);
        Assert.Contains("authors: [chief]", written);
        Assert.Contains("tags: [room]", written);
        Assert.Contains("visibility: public", written);
        Assert.Contains("The room decided.", written);
    }

    private static string Note(
        string summary = "One line.",
        string authors = "chief",
        string created = "2026-09-27",
        string source = "alex",
        string supersedes = "",
        string body = "Body.")
    {
        var sup = supersedes.Length == 0 ? "" : "supersedes: [" + supersedes + "]\n";
        return "---\nid: n\ntitle: Title\nsummary: " + summary + "\ntags: [topic]\nsource: " + source +
               "\nauthors: [" + authors + "]\ncreated: " + created + "\n" + sup + "visibility: public\n---\n\n" + body;
    }
}
