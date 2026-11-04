using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Indexing;

public sealed class SqliteNoteIndex : INoteIndex
{
    private const string SourceParameter = "$source";

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
        command.CommandText = "SELECT path, content_hash, chunker FROM notes WHERE source_id = $source";
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        using var reader = command.ExecuteReader();
        var notes = new Dictionary<string, NoteState>(StringComparer.Ordinal);
        while (reader.Read())
        {
            notes[reader.GetString(0)] = new NoteState(reader.GetString(1), reader.GetInt32(2));
        }

        return notes;
    }

    public void Upsert(Guid sourceId, ScannedFile file, string title, string contentHash, int chunker, IReadOnlyList<Chunk> chunks, DateTimeOffset at)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        Delete(connection, transaction, sourceId, file.RelativePath);

        long noteId;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO notes (source_id, path, topic, title, content_hash, chunker, indexed_at)
                VALUES ($source, $path, $topic, $title, $hash, $chunker, $at)
                RETURNING id
                """;
            insert.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
            insert.Parameters.AddWithValue("$path", file.RelativePath);
            insert.Parameters.AddWithValue("$topic", file.Topic);
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$hash", contentHash);
            insert.Parameters.AddWithValue("$chunker", chunker);
            insert.Parameters.AddWithValue("$at", StorageText.Format(at));
            noteId = (long)insert.ExecuteScalar()!;
        }

        using var chunk = connection.CreateCommand();
        chunk.Transaction = transaction;
        chunk.CommandText = "INSERT INTO chunks (note_id, ordinal, heading, text) VALUES ($note, $ordinal, $heading, $text)";
        var ordinalParameter = chunk.Parameters.AddWithValue("$ordinal", 0);
        var headingParameter = chunk.Parameters.AddWithValue("$heading", string.Empty);
        var textParameter = chunk.Parameters.AddWithValue("$text", string.Empty);
        chunk.Parameters.AddWithValue("$note", noteId);
        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            ordinalParameter.Value = ordinal;
            headingParameter.Value = chunks[ordinal].Heading;
            textParameter.Value = chunks[ordinal].Text;
            chunk.ExecuteNonQuery();
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
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }
}
