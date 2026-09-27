using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Chief.Knowledge;

public sealed class SearchRequest
{
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Authors { get; init; } = [];
    public DateOnly? Since { get; init; }
    public DateOnly? Until { get; init; }
    public string? Source { get; init; }
    public bool IncludeSuperseded { get; init; }
    public int Limit { get; init; } = 10;
}

public sealed class SearchHit
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required double Score { get; init; }
    public required string Created { get; init; }
    public required IReadOnlyList<string> Authors { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required string Source { get; init; }
    public required bool Important { get; init; }
    public required bool Superseded { get; init; }
}

public sealed class SearchOutcome
{
    public required IReadOnlyList<SearchHit> Hits { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required string Backend { get; init; }
}

public static class KnowledgeIndex
{
    public static string Rebuild(string indexPath, IReadOnlyList<NoteDocument> notes, IEmbedder embedder, IndexOptions options)
    {
        var full = Path.GetFullPath(indexPath);
        var dir = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, ".hive-building-" + Guid.NewGuid().ToString("n") + ".sqlite");
        try
        {
            var backend = BuildInto(tmp, notes, embedder, options);
            SqliteFiles.DeleteSidecars(full);
            File.Move(tmp, full, overwrite: true);
            return backend;
        }
        catch
        {
            SqliteFiles.Delete(tmp);
            throw;
        }
    }

    public static SearchOutcome Search(string indexPath, string query, SearchRequest request, IEmbedder embedder, IndexOptions options)
    {
        if (!File.Exists(indexPath))
            throw new KnowledgeException(2, "no index at " + indexPath + "; run rebuild");

        var notes = ReadNotes(indexPath);
        var superseded = new HashSet<string>(notes.SelectMany(n => n.Supersedes), StringComparer.Ordinal);
        var allowed = notes.Where(n => Passes(n, request, superseded)).ToList();
        var allowedIds = allowed.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        var warnings = new List<string>();
        var ftsRanks = FtsRanks(indexPath, query, allowedIds);
        var (vectorRanks, backend) = VectorRanks(indexPath, query, allowedIds, embedder, options, warnings);

        var hits = new List<SearchHit>();
        foreach (var note in allowed)
        {
            ftsRanks.TryGetValue(note.Id, out var fts);
            vectorRanks.TryGetValue(note.Id, out var vec);
            int? ftsRank = fts == 0 ? null : fts;
            int? vecRank = vec == 0 ? null : vec;
            var demote = request.IncludeSuperseded && superseded.Contains(note.Id);
            var score = Ranking.Fuse(ftsRank, vecRank, note.Important, Ranking.AgeDays(note.Created, options.Today), demote);
            if (score <= 0)
                continue;
            hits.Add(new SearchHit
            {
                Id = note.Id,
                Title = note.Title,
                Summary = note.Summary,
                Score = score,
                Created = note.Created.ToString("yyyy-MM-dd"),
                Authors = note.Authors,
                Tags = note.Tags,
                Source = note.Source,
                Important = note.Important,
                Superseded = superseded.Contains(note.Id)
            });
        }

        var ordered = hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .Take(request.Limit)
            .ToList();
        return new SearchOutcome { Hits = ordered, Warnings = warnings, Backend = backend };
    }

    private static bool Passes(NoteDocument note, SearchRequest request, HashSet<string> superseded)
    {
        if (!request.IncludeSuperseded && superseded.Contains(note.Id))
            return false;
        if (request.Since is { } since && note.Created < since)
            return false;
        if (request.Until is { } until && note.Created > until)
            return false;
        if (request.Source is { } source && !string.Equals(note.Source, source, StringComparison.Ordinal))
            return false;
        foreach (var tag in request.Tags)
        {
            if (!note.Tags.Contains(tag, StringComparer.Ordinal))
                return false;
        }
        foreach (var author in request.Authors)
        {
            if (!note.Authors.Contains(author, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    private static string BuildInto(string path, IReadOnlyList<NoteDocument> notes, IEmbedder embedder, IndexOptions options)
    {
        using var conn = Open(path);
        Exec(conn, """
            PRAGMA journal_mode=DELETE;
            CREATE TABLE notes (
              id TEXT PRIMARY KEY,
              title TEXT NOT NULL,
              summary TEXT NOT NULL,
              body TEXT NOT NULL,
              tags TEXT NOT NULL,
              source TEXT NOT NULL,
              authors TEXT NOT NULL,
              created TEXT NOT NULL,
              supersedes TEXT NOT NULL,
              links TEXT NOT NULL,
              visibility TEXT NOT NULL,
              important INTEGER NOT NULL,
              path TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE notes_fts USING fts5(
              id UNINDEXED,
              title,
              summary,
              body,
              tags,
              source,
              tokenize = 'unicode61 remove_diacritics 2'
            );
            CREATE TABLE vectors (
              id TEXT PRIMARY KEY,
              model_id TEXT NOT NULL,
              dim INTEGER NOT NULL,
              embedding BLOB NOT NULL
            );
            CREATE TABLE meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            """);

        var vecPath = options.Backend == VectorBackendPreference.Pure
            ? null
            : options.Vec0Path ?? Vec0Locator.Resolve(Environment.GetEnvironmentVariable("CHIEF_KNOWLEDGE_VEC0"), AppContext.BaseDirectory);
        var useVec = options.Backend != VectorBackendPreference.Pure && Vec0Locator.TryLoad(conn, vecPath);
        var dim = embedder.Dimensions;
        if (useVec)
        {
            if (dim is < 1 or > 4096)
                throw new KnowledgeException(2, "embedding dimension is out of range");
            Exec(conn, "CREATE TABLE vec_map (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE)");
            Exec(conn, $"CREATE VIRTUAL TABLE vec_notes USING vec0(embedding float[{dim}] distance_metric=cosine)");
        }

        using var tx = conn.BeginTransaction();
        foreach (var note in notes)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO notes (id, title, summary, body, tags, source, authors, created, supersedes, links, visibility, important, path)
                    VALUES ($id, $title, $summary, $body, $tags, $source, $authors, $created, $supersedes, $links, $visibility, $important, $path)
                    """;
                cmd.Parameters.AddWithValue("$id", note.Id);
                cmd.Parameters.AddWithValue("$title", note.Title);
                cmd.Parameters.AddWithValue("$summary", note.Summary);
                cmd.Parameters.AddWithValue("$body", note.Body);
                cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(note.Tags));
                cmd.Parameters.AddWithValue("$source", note.Source);
                cmd.Parameters.AddWithValue("$authors", JsonSerializer.Serialize(note.Authors));
                cmd.Parameters.AddWithValue("$created", note.Created.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$supersedes", JsonSerializer.Serialize(note.Supersedes));
                cmd.Parameters.AddWithValue("$links", JsonSerializer.Serialize(note.Links));
                cmd.Parameters.AddWithValue("$visibility", note.Visibility);
                cmd.Parameters.AddWithValue("$important", note.Important ? 1 : 0);
                cmd.Parameters.AddWithValue("$path", note.Path);
                cmd.ExecuteNonQuery();
            }
            using (var fts = conn.CreateCommand())
            {
                fts.Transaction = tx;
                fts.CommandText = "INSERT INTO notes_fts (id, title, summary, body, tags, source) VALUES ($id, $title, $summary, $body, $tags, $source)";
                fts.Parameters.AddWithValue("$id", note.Id);
                fts.Parameters.AddWithValue("$title", note.Title);
                fts.Parameters.AddWithValue("$summary", note.Summary);
                fts.Parameters.AddWithValue("$body", note.Body);
                fts.Parameters.AddWithValue("$tags", string.Join(' ', note.Tags));
                fts.Parameters.AddWithValue("$source", note.Source);
                fts.ExecuteNonQuery();
            }

            var vector = VectorMath.Normalize(embedder.Embed(note.EmbeddingInput));
            if (vector.Length != dim)
                throw new KnowledgeException(2, "embedder returned a vector of the wrong length");
            using (var vec = conn.CreateCommand())
            {
                vec.Transaction = tx;
                vec.CommandText = "INSERT INTO vectors (id, model_id, dim, embedding) VALUES ($id, $model, $dim, $emb)";
                vec.Parameters.AddWithValue("$id", note.Id);
                vec.Parameters.AddWithValue("$model", embedder.ModelId);
                vec.Parameters.AddWithValue("$dim", vector.Length);
                vec.Parameters.AddWithValue("$emb", VectorMath.ToBlob(vector));
                vec.ExecuteNonQuery();
            }
            if (useVec)
            {
                long rowid;
                using (var map = conn.CreateCommand())
                {
                    map.Transaction = tx;
                    map.CommandText = "INSERT INTO vec_map (id) VALUES ($id)";
                    map.Parameters.AddWithValue("$id", note.Id);
                    map.ExecuteNonQuery();
                }
                using (var idCmd = conn.CreateCommand())
                {
                    idCmd.Transaction = tx;
                    idCmd.CommandText = "SELECT last_insert_rowid()";
                    rowid = (long)(idCmd.ExecuteScalar() ?? throw new KnowledgeException(2, "vec_map insert failed"));
                }
                using var knn = conn.CreateCommand();
                knn.Transaction = tx;
                knn.CommandText = "INSERT INTO vec_notes (rowid, embedding) VALUES ($rowid, $emb)";
                knn.Parameters.AddWithValue("$rowid", rowid);
                knn.Parameters.AddWithValue("$emb", VectorMath.ToBlob(vector));
                knn.ExecuteNonQuery();
            }
        }

        void Meta(string key, string value)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
        var backend = useVec ? "vec0" : "pure";
        Meta("schema", "1");
        Meta("model_id", embedder.ModelId);
        Meta("backend", backend);
        Meta("dim", dim.ToString());
        tx.Commit();
        return backend;
    }

    private static Dictionary<string, int> FtsRanks(string indexPath, string query, HashSet<string> allowed)
    {
        var match = Ranking.FtsMatch(query);
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (match is null || allowed.Count == 0)
            return ranks;

        using var conn = Open(indexPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, bm25(notes_fts) AS s FROM notes_fts WHERE notes_fts MATCH $q ORDER BY s ASC, id ASC";
        cmd.Parameters.AddWithValue("$q", match);
        var ordered = new List<(string Id, double Score)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!allowed.Contains(id))
                    continue;
                ordered.Add((id, reader.GetDouble(1)));
            }
        }
        AssignRanks(ordered, lowerIsBetter: true, ranks);
        return ranks;
    }

    private static (Dictionary<string, int> Ranks, string Backend) VectorRanks(
        string indexPath,
        string query,
        HashSet<string> allowed,
        IEmbedder embedder,
        IndexOptions options,
        List<string> warnings)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (allowed.Count == 0)
            return (ranks, "none");

        var queryVec = VectorMath.Normalize(embedder.Embed(query));
        using var conn = Open(indexPath);
        var storedModel = Meta(conn, "model_id");
        var backend = Meta(conn, "backend") ?? "pure";
        if (!string.Equals(storedModel, embedder.ModelId, StringComparison.Ordinal))
        {
            warnings.Add(
                "index vectors are model " + (storedModel ?? "(missing)") +
                " and this query uses " + embedder.ModelId +
                "; vector scores were skipped so the two are not mixed. Rebuild to refresh them.");
            return (ranks, "skipped");
        }

        var rows = new List<(string Id, float[] Vector, string ModelId)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, model_id, dim, embedding FROM vectors";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!allowed.Contains(id))
                    continue;
                var model = reader.GetString(1);
                var dim = reader.GetInt32(2);
                var blob = (byte[])reader.GetValue(3);
                var vector = VectorMath.FromBlob(blob);
                if (!string.Equals(model, embedder.ModelId, StringComparison.Ordinal) || vector.Length != dim || vector.Length != queryVec.Length)
                {
                    warnings.Add("skipped vector for " + id + " because its model id or dimension does not match " + embedder.ModelId);
                    continue;
                }
                rows.Add((id, vector, model));
            }
        }

        if (backend == "vec0" && options.Backend != VectorBackendPreference.Pure && rows.Count > 0)
        {
            var vecPath = options.Vec0Path ?? Vec0Locator.Resolve(Environment.GetEnvironmentVariable("CHIEF_KNOWLEDGE_VEC0"), AppContext.BaseDirectory);
            if (Vec0Locator.TryLoad(conn, vecPath) && TryVec0Ranks(conn, queryVec, rows, ranks))
                return (ranks, "vec0");
            warnings.Add("sqlite-vec was not usable for this query; ranked vectors in process");
            ranks.Clear();
        }

        var scored = rows.Select(r => (r.Id, Score: VectorMath.Cosine(queryVec, r.Vector))).ToList();
        AssignRanks(scored, lowerIsBetter: false, ranks);
        return (ranks, "pure");
    }

    private static bool TryVec0Ranks(SqliteConnection conn, float[] query, List<(string Id, float[] Vector, string ModelId)> rows, Dictionary<string, int> ranks)
    {
        try
        {
            var dim = query.Length;
            Exec(conn, $"CREATE VIRTUAL TABLE temp.knn USING vec0(embedding float[{dim}] distance_metric=cosine)");
            using (var tx = conn.BeginTransaction())
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO temp.knn (rowid, embedding) VALUES ($id, $emb)";
                    ins.Parameters.AddWithValue("$id", i + 1);
                    ins.Parameters.AddWithValue("$emb", VectorMath.ToBlob(rows[i].Vector));
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }

            var ordered = new List<(string Id, double Score)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT rowid, distance FROM temp.knn WHERE embedding MATCH $q AND k = $k";
                cmd.Parameters.AddWithValue("$q", VectorMath.ToBlob(query));
                cmd.Parameters.AddWithValue("$k", rows.Count);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var rowid = (int)reader.GetInt64(0) - 1;
                    if (rowid < 0 || rowid >= rows.Count)
                        continue;
                    ordered.Add((rows[rowid].Id, reader.GetDouble(1)));
                }
            }
            Exec(conn, "DROP TABLE temp.knn");
            if (ordered.Count != rows.Count)
                return false;
            AssignRanks(ordered, lowerIsBetter: true, ranks);
            return true;
        }
        catch (SqliteException)
        {
            TryDrop(conn, "DROP TABLE IF EXISTS temp.knn");
            return false;
        }
    }

    private static void AssignRanks(List<(string Id, double Score)> ordered, bool lowerIsBetter, Dictionary<string, int> ranks)
    {
        ordered.Sort((a, b) =>
        {
            var c = a.Score.CompareTo(b.Score);
            if (!lowerIsBetter)
                c = -c;
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        for (var i = 0; i < ordered.Count; i++)
            ranks[ordered[i].Id] = i + 1;
    }

    private static List<NoteDocument> ReadNotes(string indexPath)
    {
        using var conn = Open(indexPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, summary, body, tags, source, authors, created, supersedes, links, visibility, important, path FROM notes";
        var notes = new List<NoteDocument>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            notes.Add(new NoteDocument
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Summary = reader.GetString(2),
                Body = reader.GetString(3),
                Tags = JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [],
                Source = reader.GetString(5),
                Authors = JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
                Created = DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Supersedes = JsonSerializer.Deserialize<string[]>(reader.GetString(8)) ?? [],
                Links = JsonSerializer.Deserialize<string[]>(reader.GetString(9)) ?? [],
                Visibility = reader.GetString(10),
                Important = reader.GetInt32(11) != 0,
                Path = reader.GetString(12)
            });
        }
        return notes;
    }

    private static string? Meta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection("Data Source=" + path);
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryDrop(SqliteConnection conn, string sql)
    {
        try
        {
            Exec(conn, sql);
        }
        catch (SqliteException)
        {
            // The temp table is only an acceleration path.
        }
    }
}

static class SqliteFiles
{
    public static void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        DeleteSidecars(path);
    }

    public static void DeleteSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = path + suffix;
            if (File.Exists(sidecar))
                File.Delete(sidecar);
        }
    }
}
