using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Indexing;

public sealed class SqliteNoteIndex : INoteIndex, IChunkHits
{
    private const string SourceParameter = "$source";
    private const string PathParameter = "$path";
    private const string NoteParameter = "$note";

    /// <summary>The most hits one search returns, whatever is asked.</summary>

    /// <summary>How many words of a chunk the snippet of a hit shows around the match.</summary>
    private const int SnippetWords = 32;
    private const int MeaningSnippetLength = 240;
    private readonly IDatabase _database;
    private readonly Embeddings.EmbeddingWakeUp? _wake;

    public SqliteNoteIndex(IDatabase database, Embeddings.EmbeddingWakeUp? wake = null)
    {
        _database = database;
        _wake = wake;
    }

    public IReadOnlyDictionary<string, NoteState> Notes(Guid sourceId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, content_hash, chunker, meta_version, size, modified_ticks FROM notes WHERE source_id = $source";
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        using var reader = command.ExecuteReader();
        var notes = new Dictionary<string, NoteState>(StringComparer.Ordinal);
        while (reader.Read())
        {
            notes[reader.GetString(0)] = new NoteState(reader.GetString(1), reader.GetInt32(2), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(3));
        }

        return notes;
    }

    public void Upsert(Guid sourceId, ScannedFile file, IndexedNote note, DateTimeOffset at)
    {
        bool removed;
        using (var connection = _database.Open())
        {
            using var transaction = connection.BeginTransaction();
            var existing = FindNote(connection, transaction, sourceId, file.RelativePath);
            long noteId;
            if (existing is { } found)
            {
                // The note stays, and so do its chunks that did not change (ADR 0039): a conversation that grows has the chunks it had and new ones, and the vectors that hang on the old
                // ones are still good. Only what differs is written, and the model only reads that.
                noteId = found.Id;
                UpdateNote(connection, transaction, noteId, file, note, at);
                removed = SyncChunks(connection, transaction, noteId, note.Chunks, found.Title != note.Title);
                Execute(connection, transaction, "DELETE FROM note_links WHERE note_id = $note; DELETE FROM note_tags WHERE note_id = $note", noteId);
            }
            else
            {
                noteId = InsertNote(connection, transaction, sourceId, file, note, at);
                removed = false;
                InsertChunks(connection, transaction, noteId, note.Chunks);
            }

            InsertLinks(connection, transaction, noteId, note.Meta.Links);
            InsertTags(connection, transaction, noteId, note.Meta.Tags);
            transaction.Commit();
        }

        _wake?.Notify();
        if (removed)
        {
            _wake?.NotifyRemoved();
        }
    }

    private static (long Id, string Title)? FindNote(SqliteConnection connection, SqliteTransaction transaction, Guid sourceId, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, title FROM notes WHERE source_id = $source AND path = $path";
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        command.Parameters.AddWithValue(PathParameter, path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    private static long InsertNote(SqliteConnection connection, SqliteTransaction transaction, Guid sourceId, ScannedFile file, IndexedNote note, DateTimeOffset at)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO notes (source_id, path, topic, title, name, content_hash, chunker, meta_version, size, modified_ticks, indexed_at)
            VALUES ($source, $path, $topic, $title, $name, $hash, $chunker, $metaVersion, $size, $modified, $at)
            RETURNING id
            """;
        insert.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        insert.Parameters.AddWithValue(PathParameter, file.RelativePath);
        AddNoteValues(insert, file, note, at);
        return (long)insert.ExecuteScalar()!;
    }

    private static void UpdateNote(SqliteConnection connection, SqliteTransaction transaction, long noteId, ScannedFile file, IndexedNote note, DateTimeOffset at)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE notes SET topic = $topic, title = $title, name = $name, content_hash = $hash, chunker = $chunker, meta_version = $metaVersion, size = $size, modified_ticks = $modified, indexed_at = $at
            WHERE id = $id
            """;
        update.Parameters.AddWithValue("$id", noteId);
        AddNoteValues(update, file, note, at);
        update.ExecuteNonQuery();
    }

    private static void AddNoteValues(SqliteCommand command, ScannedFile file, IndexedNote note, DateTimeOffset at)
    {
        command.Parameters.AddWithValue("$topic", file.Topic);
        command.Parameters.AddWithValue("$title", note.Title);
        command.Parameters.AddWithValue("$name", System.IO.Path.GetFileNameWithoutExtension(file.RelativePath).ToLowerInvariant());
        command.Parameters.AddWithValue("$hash", note.ContentHash);
        command.Parameters.AddWithValue("$chunker", note.Chunker);
        command.Parameters.AddWithValue("$metaVersion", note.MetaVersion);
        command.Parameters.AddWithValue("$size", file.Length);
        command.Parameters.AddWithValue("$modified", file.ModifiedTicks);
        command.Parameters.AddWithValue("$at", StorageText.Format(at));
    }

    // Makes the chunks of a note the given ones, keeping the rows of the chunks that are in both (same heading, same text, whatever their place) and writing only the others. A new
    // title changes the text the model reads for every chunk of the note, so their vectors are forgotten (the cache brings back the ones that are still good). Returns whether chunks went.
    private static bool SyncChunks(SqliteConnection connection, SqliteTransaction transaction, long noteId, IReadOnlyList<Chunk> chunks, bool titleChanged)
    {
        var existing = new Dictionary<(string Heading, string Text), Queue<(long Id, int Ordinal)>>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, ordinal, heading, text FROM chunks WHERE note_id = $note ORDER BY ordinal";
            read.Parameters.AddWithValue(NoteParameter, noteId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(2), reader.GetString(3));
                if (!existing.TryGetValue(key, out var queue))
                {
                    existing[key] = queue = new Queue<(long, int)>();
                }

                queue.Enqueue((reader.GetInt64(0), reader.GetInt32(1)));
            }
        }

        var added = new List<(int Ordinal, Chunk Chunk)>();
        using var reorder = connection.CreateCommand();
        reorder.Transaction = transaction;
        reorder.CommandText = "UPDATE chunks SET ordinal = $ordinal WHERE id = $id AND ordinal <> $ordinal";
        var ordinalParameter = reorder.Parameters.AddWithValue("$ordinal", 0);
        var idParameter = reorder.Parameters.AddWithValue("$id", 0L);
        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            if (existing.TryGetValue((chunks[ordinal].Heading, chunks[ordinal].Text), out var queue) && queue.Count > 0)
            {
                ordinalParameter.Value = ordinal;
                idParameter.Value = queue.Dequeue().Id;
                reorder.ExecuteNonQuery();
            }
            else
            {
                added.Add((ordinal, chunks[ordinal]));
            }
        }

        var gone = existing.Values.SelectMany(queue => queue).Select(item => item.Id).ToList();
        if (gone.Count > 0)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM chunks WHERE id IN (SELECT value FROM json_each($ids))";
            delete.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(gone));
            delete.ExecuteNonQuery();
        }

        foreach (var (ordinal, chunk) in added)
        {
            InsertChunk(connection, transaction, noteId, ordinal, chunk);
        }

        if (titleChanged)
        {
            Execute(connection, transaction, "DELETE FROM chunk_embeddings WHERE chunk_id IN (SELECT id FROM chunks WHERE note_id = $note)", noteId);
        }

        return gone.Count > 0 || titleChanged;
    }

    private static void InsertChunks(SqliteConnection connection, SqliteTransaction transaction, long noteId, IReadOnlyList<Chunk> chunks)
    {
        for (var index = 0; index < chunks.Count; index++)
        {
            InsertChunk(connection, transaction, noteId, index, chunks[index]);
        }
    }

    private static void InsertChunk(SqliteConnection connection, SqliteTransaction transaction, long noteId, int ordinal, Chunk chunk)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO chunks (note_id, ordinal, heading, text) VALUES ($note, $ordinal, $heading, $text)";
        command.Parameters.AddWithValue(NoteParameter, noteId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$heading", chunk.Heading);
        command.Parameters.AddWithValue("$text", chunk.Text);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, long noteId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(NoteParameter, noteId);
        command.ExecuteNonQuery();
    }

    private static void InsertLinks(SqliteConnection connection, SqliteTransaction transaction, long noteId, IEnumerable<WikiLink> links) =>
        InsertEach(connection, transaction, "INSERT INTO note_links (note_id, target) VALUES ($note, $value)", noteId, links.Select(link => link.Target).Distinct(StringComparer.Ordinal));

    private static void InsertTags(SqliteConnection connection, SqliteTransaction transaction, long noteId, IEnumerable<string> tags) =>
        InsertEach(connection, transaction, "INSERT INTO note_tags (note_id, tag) VALUES ($note, $value)", noteId, tags);

    private static void InsertEach(SqliteConnection connection, SqliteTransaction transaction, string sql, long noteId, IEnumerable<string> values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(NoteParameter, noteId);
        var value = command.Parameters.AddWithValue("$value", string.Empty);
        foreach (var text in values)
        {
            value.Value = text;
            command.ExecuteNonQuery();
        }
    }

    public void Touch(Guid sourceId, ScannedFile file)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notes SET size = $size, modified_ticks = $modified WHERE source_id = $source AND path = $path";
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        command.Parameters.AddWithValue(PathParameter, file.RelativePath);
        command.Parameters.AddWithValue("$size", file.Length);
        command.Parameters.AddWithValue("$modified", file.ModifiedTicks);
        command.ExecuteNonQuery();
    }

    public void UpdateMeta(Guid sourceId, ScannedFile file, NoteMeta meta, int metaVersion)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE notes SET meta_version = $version, name = $name, size = $size, modified_ticks = $modified WHERE source_id = $source AND path = $path RETURNING id";
        update.Parameters.AddWithValue("$version", metaVersion);
        // The name is written here too: a database made before the names were kept has none, and this is the pass that gives them.
        update.Parameters.AddWithValue("$name", System.IO.Path.GetFileNameWithoutExtension(file.RelativePath).ToLowerInvariant());
        update.Parameters.AddWithValue("$size", file.Length);
        update.Parameters.AddWithValue("$modified", file.ModifiedTicks);
        update.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        update.Parameters.AddWithValue(PathParameter, file.RelativePath);
        if (update.ExecuteScalar() is not long noteId)
        {
            return;
        }

        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM note_links WHERE note_id = $note; DELETE FROM note_tags WHERE note_id = $note";
        clear.Parameters.AddWithValue(NoteParameter, noteId);
        clear.ExecuteNonQuery();

        InsertLinks(connection, transaction, noteId, meta.Links);
        InsertTags(connection, transaction, noteId, meta.Tags);
        transaction.Commit();
    }

    public IReadOnlySet<string> Images(Guid sourceId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM images WHERE source_id = $source";
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        using var reader = command.ExecuteReader();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    public void ReplaceImages(Guid sourceId, IReadOnlyCollection<string> paths)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM images WHERE source_id = $source";
        clear.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        clear.ExecuteNonQuery();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO images (source_id, path, name) VALUES ($source, $path, $name)";
        insert.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        var path = insert.Parameters.AddWithValue(PathParameter, string.Empty);
        var name = insert.Parameters.AddWithValue("$name", string.Empty);
        foreach (var image in paths)
        {
            path.Value = image;
            name.Value = System.IO.Path.GetFileName(image).ToLowerInvariant();
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Remove(Guid sourceId, string path)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        Delete(connection, transaction, sourceId, path);
        transaction.Commit();
        _wake?.NotifyRemoved();
    }

    public IndexCounts Counts(Guid sourceId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM notes WHERE source_id = $source),
                   (SELECT COUNT(*) FROM chunks JOIN notes ON notes.id = chunks.note_id WHERE notes.source_id = $source),
                   (SELECT MAX(indexed_at) FROM notes WHERE source_id = $source)
            """;
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        using var reader = command.ExecuteReader();
        reader.Read();
        DateTimeOffset? last = reader.IsDBNull(2) ? null : StorageText.Parse(reader.GetString(2));
        return new IndexCounts(reader.GetInt32(0), reader.GetInt32(1), last);
    }

    public IReadOnlyList<SearchHit> Search(SearchRequest request, SourceScope scope)
    {
        var match = FtsQuery.From(request.Query);
        if (match is null)
        {
            return [];
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, path, topic, title, heading, snippet, score, chunk FROM (
              SELECT id, name, path, topic, title, heading, snippet, score, chunk, rank, ROW_NUMBER() OVER (PARTITION BY note ORDER BY rank, chunk) AS place FROM (
                SELECT s.id AS id, s.name AS name, n.path AS path, n.topic AS topic, n.title AS title, c.heading AS heading,
                       snippet(chunks_fts, 0, '', '', ' … ', $snippetWords) AS snippet, -bm25(chunks_fts) AS score, c.id AS chunk, n.id AS note, bm25(chunks_fts) AS rank
                FROM chunks_fts
                JOIN chunks c ON c.id = chunks_fts.rowid
                JOIN notes n ON n.id = c.note_id
                JOIN sources s ON s.id = n.source_id
                WHERE chunks_fts MATCH $match
                  AND ($source IS NULL OR s.id = $source)
                  AND ($topics IS NULL OR n.topic IN (SELECT value FROM json_each($topics)))
                  AND n.topic NOT IN (SELECT value FROM json_each($notTopics))
                  AND s.id NOT IN (SELECT value FROM json_each($notSources))
                  AND ($sources IS NULL OR s.id IN (SELECT value FROM json_each($sources))) AND ($hideConversations = 0 OR s.kind <> $conversationsKind)
                ORDER BY bm25(chunks_fts)
                LIMIT $candidates
              )
            )
            WHERE place <= $perNote
            ORDER BY rank, chunk
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue(SourceParameter, request.SourceId?.ToString() ?? (object)DBNull.Value);
        BindFilters(command, request);
        // A caller limited to some sources sees those, even when it is none: an empty list matches nothing, never everything.
        command.Parameters.AddWithValue("$sources", scope.Sources is { } allowed ? JsonSerializer.Serialize(allowed.Select(id => id.ToString())) : DBNull.Value);
        // What was said to the assistants is seen only by those who hold conversations.read.
        command.Parameters.AddWithValue("$hideConversations", scope.ConversationsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$conversationsKind", nameof(Mnemarr.Core.Sources.SourceKind.Conversations));
        command.Parameters.AddWithValue("$limit", Math.Clamp(request.Limit, 1, SearchRequest.MaxLimit));
        command.Parameters.AddWithValue("$candidates", Candidates);
        command.Parameters.AddWithValue("$perNote", Math.Clamp(request.PerNote, 1, SearchRequest.MaxLimit));
        command.Parameters.AddWithValue("$snippetWords", SnippetWords);

        using var reader = command.ExecuteReader();
        var hits = new List<SearchHit>();
        while (reader.Read())
        {
            hits.Add(new SearchHit(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetDouble(7), reader.GetInt64(8)));
        }

        return hits;
    }

    // The keyword list is cut from the best passages of the whole index, wide enough that a note with hundreds of passages cannot take the place of the others once each note is held to its share.
    private const int Candidates = 600;

    private static void BindFilters(SqliteCommand command, SearchRequest request)
    {
        var within = request.StayWithin;
        command.Parameters.AddWithValue("$topics", within.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(within));
        command.Parameters.AddWithValue("$notTopics", JsonSerializer.Serialize(request.ExcludeTopics));
        command.Parameters.AddWithValue("$notSources", JsonSerializer.Serialize(request.ExcludeSources.Select(id => id.ToString())));
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchHit> Chunks(IReadOnlyCollection<long> chunkIds, SearchRequest request, SourceScope scope)
    {
        if (chunkIds.Count == 0)
        {
            return [];
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, n.path, n.topic, n.title, c.heading, substr(c.text, 1, $snippetLength), c.id
            FROM chunks c
            JOIN notes n ON n.id = c.note_id
            JOIN sources s ON s.id = n.source_id
            WHERE c.id IN (SELECT value FROM json_each($ids))
              AND ($source IS NULL OR s.id = $source)
              AND ($topics IS NULL OR n.topic IN (SELECT value FROM json_each($topics)))
              AND n.topic NOT IN (SELECT value FROM json_each($notTopics))
              AND s.id NOT IN (SELECT value FROM json_each($notSources))
              AND ($sources IS NULL OR s.id IN (SELECT value FROM json_each($sources))) AND ($hideConversations = 0 OR s.kind <> $conversationsKind)
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(chunkIds));
        command.Parameters.AddWithValue(SourceParameter, request.SourceId?.ToString() ?? (object)DBNull.Value);
        BindFilters(command, request);
        command.Parameters.AddWithValue("$sources", scope.Sources is { } allowed ? JsonSerializer.Serialize(allowed.Select(id => id.ToString())) : DBNull.Value);
        command.Parameters.AddWithValue("$hideConversations", scope.ConversationsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$conversationsKind", nameof(Mnemarr.Core.Sources.SourceKind.Conversations));
        command.Parameters.AddWithValue("$snippetLength", MeaningSnippetLength);
        using var reader = command.ExecuteReader();
        var hits = new List<SearchHit>();
        while (reader.Read())
        {
            hits.Add(new SearchHit(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), 0, reader.GetInt64(7), SearchMatch.Meaning));
        }

        return hits;
    }

    private static void Delete(SqliteConnection connection, SqliteTransaction transaction, Guid sourceId, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM notes WHERE source_id = $source AND path = $path";
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        command.Parameters.AddWithValue(PathParameter, path);
        command.ExecuteNonQuery();
    }
}
