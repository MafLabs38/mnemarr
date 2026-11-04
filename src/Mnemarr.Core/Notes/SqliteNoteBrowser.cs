using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Notes;

public sealed class SqliteNoteBrowser : INoteBrowser
{
    /// <summary>The most notes the tree lists: a vault of this size is the limit of a tree, and the search is the way in beyond it.</summary>
    internal const int MaxEntries = 10_000;

    private const int MaxTags = 200;
    private const string ScopeFilter = "($sources IS NULL OR s.id IN (SELECT value FROM json_each($sources)))";
    private readonly IDatabase _database;

    public SqliteNoteBrowser(IDatabase database)
    {
        _database = database;
    }

    public IReadOnlyList<NoteEntry> List(SourceScope scope, Guid? sourceId, string? tag)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.name, n.path, n.title, n.topic
            FROM notes n JOIN sources s ON s.id = n.source_id
            WHERE {ScopeFilter}
              AND ($source IS NULL OR s.id = $source)
              AND ($tag IS NULL OR EXISTS (SELECT 1 FROM note_tags t WHERE t.note_id = n.id AND t.tag = $tag))
            ORDER BY s.name, n.path
            LIMIT $limit
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$source", sourceId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$tag", tag?.ToLowerInvariant() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", MaxEntries);
        using var reader = command.ExecuteReader();
        var entries = new List<NoteEntry>();
        while (reader.Read())
        {
            entries.Add(new NoteEntry(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return entries;
    }

    public IReadOnlyList<TagCount> Tags(SourceScope scope)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.tag, COUNT(*) AS used
            FROM note_tags t JOIN notes n ON n.id = t.note_id JOIN sources s ON s.id = n.source_id
            WHERE {ScopeFilter}
            GROUP BY t.tag
            ORDER BY used DESC, t.tag
            LIMIT $limit
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$limit", MaxTags);
        using var reader = command.ExecuteReader();
        var tags = new List<TagCount>();
        while (reader.Read())
        {
            tags.Add(new TagCount(reader.GetString(0), reader.GetInt32(1)));
        }

        return tags;
    }

    public NoteDetail? Find(Guid sourceId, string path, SourceScope scope)
    {
        using var connection = _database.Open();
        var note = ReadNote(connection, sourceId, path, scope);
        if (note is null)
        {
            return null;
        }

        var (id, reference, name) = note.Value;
        var pathWithoutExtension = WithoutExtension(reference.Path).ToLowerInvariant();
        return new NoteDetail(
            reference,
            Column(connection, "SELECT DISTINCT tag FROM note_tags WHERE note_id = $note ORDER BY tag", id),
            [.. Column(connection, "SELECT DISTINCT target FROM note_links WHERE note_id = $note ORDER BY target", id).Select(target => new ResolvedLink(target, Resolve(connection, target, sourceId, scope)))],
            Backlinks(connection, id, name, pathWithoutExtension, scope));
    }

    private static (long Id, NoteRef Reference, string Name)? ReadNote(SqliteConnection connection, Guid sourceId, string path, SourceScope scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT n.id, s.name, n.path, n.title, n.name
            FROM notes n JOIN sources s ON s.id = n.source_id
            WHERE s.id = $source AND n.path = $path AND {ScopeFilter}
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$source", sourceId.ToString());
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), new NoteRef(sourceId, reader.GetString(1), reader.GetString(2), reader.GetString(3)), reader.GetString(4))
            : null;
    }

    /// <summary>
    /// The note a link points at. A name (<c>[[docker]]</c>) is the name of a file anywhere in the sources, a path (<c>[[devops/docker]]</c>)
    /// is the end of a path. When several notes answer, the one in the same source wins, then the shortest path, then the source name: the
    /// answer is the same every time.
    /// </summary>
    private static NoteRef? Resolve(SqliteConnection connection, string target, Guid fromSource, SourceScope scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.name, n.path, n.title
            FROM notes n JOIN sources s ON s.id = n.source_id
            WHERE {ScopeFilter}
              AND (n.name = $target
                   OR lower(n.path) = $target || '.md'
                   OR lower(n.path) = $target || '.markdown'
                   OR (instr($target, '/') > 0 AND (lower(n.path) LIKE '%/' || $escaped || '.md' ESCAPE '\' OR lower(n.path) LIKE '%/' || $escaped || '.markdown' ESCAPE '\')))
            ORDER BY (s.id = $from) DESC, length(n.path), s.name, n.path
            LIMIT 1
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$escaped", target.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal));
        command.Parameters.AddWithValue("$from", fromSource.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new NoteRef(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
    }

    private static List<NoteRef> Backlinks(SqliteConnection connection, long noteId, string name, string pathWithoutExtension, SourceScope scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT s.id, s.name, n.path, n.title
            FROM note_links l JOIN notes n ON n.id = l.note_id JOIN sources s ON s.id = n.source_id
            WHERE {ScopeFilter}
              AND n.id <> $note
              AND (l.target = $name
                   OR l.target = $path
                   OR (instr(l.target, '/') > 0 AND substr($path, -(length(l.target) + 1)) = '/' || l.target))
            ORDER BY s.name, n.path
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$note", noteId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$path", pathWithoutExtension);
        using var reader = command.ExecuteReader();
        var notes = new List<NoteRef>();
        while (reader.Read())
        {
            notes.Add(new NoteRef(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return notes;
    }

    private static List<string> Column(SqliteConnection connection, string sql, long noteId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$note", noteId);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static string WithoutExtension(string path) => path[..^Path.GetExtension(path).Length];

    private static void AddScope(SqliteCommand command, SourceScope scope) =>
        // A caller limited to some sources sees those, even when it is none: an empty list matches nothing, never everything.
        command.Parameters.AddWithValue("$sources", scope.Sources is { } allowed ? JsonSerializer.Serialize(allowed.Select(id => id.ToString())) : DBNull.Value);
}
