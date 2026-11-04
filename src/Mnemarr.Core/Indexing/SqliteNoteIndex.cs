using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Indexing;

public sealed class SqliteNoteIndex : INoteIndex
{
    private const string SourceParameter = "$source";
    private const string PathParameter = "$path";
    private const string NoteParameter = "$note";

    /// <summary>The most hits one search returns, whatever is asked.</summary>
    private const int MaxLimit = 50;

    /// <summary>How many words of a chunk the snippet of a hit shows around the match.</summary>
    private const int SnippetWords = 32;
    private readonly IDatabase _database;

    public SqliteNoteIndex(IDatabase database)
    {
        _database = database;
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
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        Delete(connection, transaction, sourceId, file.RelativePath);

        long noteId;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO notes (source_id, path, topic, title, name, content_hash, chunker, meta_version, size, modified_ticks, indexed_at)
                VALUES ($source, $path, $topic, $title, $name, $hash, $chunker, $metaVersion, $size, $modified, $at)
                RETURNING id
                """;
            insert.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
            insert.Parameters.AddWithValue(PathParameter, file.RelativePath);
            insert.Parameters.AddWithValue("$topic", file.Topic);
            insert.Parameters.AddWithValue("$title", note.Title);
            insert.Parameters.AddWithValue("$name", System.IO.Path.GetFileNameWithoutExtension(file.RelativePath).ToLowerInvariant());
            insert.Parameters.AddWithValue("$hash", note.ContentHash);
            insert.Parameters.AddWithValue("$chunker", note.Chunker);
            insert.Parameters.AddWithValue("$metaVersion", note.MetaVersion);
            insert.Parameters.AddWithValue("$size", file.Length);
            insert.Parameters.AddWithValue("$modified", file.ModifiedTicks);
            insert.Parameters.AddWithValue("$at", StorageText.Format(at));
            noteId = (long)insert.ExecuteScalar()!;
        }

        using var chunk = connection.CreateCommand();
        chunk.Transaction = transaction;
        chunk.CommandText = "INSERT INTO chunks (note_id, ordinal, heading, text) VALUES ($note, $ordinal, $heading, $text)";
        var ordinalParameter = chunk.Parameters.AddWithValue("$ordinal", 0);
        var headingParameter = chunk.Parameters.AddWithValue("$heading", string.Empty);
        var textParameter = chunk.Parameters.AddWithValue("$text", string.Empty);
        chunk.Parameters.AddWithValue(NoteParameter, noteId);
        for (var ordinal = 0; ordinal < note.Chunks.Count; ordinal++)
        {
            ordinalParameter.Value = ordinal;
            headingParameter.Value = note.Chunks[ordinal].Heading;
            textParameter.Value = note.Chunks[ordinal].Text;
            chunk.ExecuteNonQuery();
        }

        InsertLinks(connection, transaction, noteId, note.Meta.Links);
        InsertTags(connection, transaction, noteId, note.Meta.Tags);
        transaction.Commit();
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
            SELECT s.id, s.name, n.path, n.topic, n.title, c.heading,
                   snippet(chunks_fts, 0, '', '', ' … ', $snippetWords), -bm25(chunks_fts)
            FROM chunks_fts
            JOIN chunks c ON c.id = chunks_fts.rowid
            JOIN notes n ON n.id = c.note_id
            JOIN sources s ON s.id = n.source_id
            WHERE chunks_fts MATCH $match
              AND ($source IS NULL OR s.id = $source)
              AND ($topic IS NULL OR n.topic = $topic)
              AND ($sources IS NULL OR s.id IN (SELECT value FROM json_each($sources)))
            ORDER BY bm25(chunks_fts)
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue(SourceParameter, request.SourceId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$topic", request.Topic ?? (object)DBNull.Value);
        // A caller limited to some sources sees those, even when it is none: an empty list matches nothing, never everything.
        command.Parameters.AddWithValue("$sources", scope.Sources is { } allowed ? JsonSerializer.Serialize(allowed.Select(id => id.ToString())) : DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(request.Limit, 1, MaxLimit));
        command.Parameters.AddWithValue("$snippetWords", SnippetWords);

        using var reader = command.ExecuteReader();
        var hits = new List<SearchHit>();
        while (reader.Read())
        {
            hits.Add(new SearchHit(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetDouble(7)));
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
