using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Notes;

public sealed class SqliteNoteBrowser : INoteBrowser
{
    /// <summary>The most notes the tree lists for one source: a vault of this size is the limit of a tree, and the search is the way in beyond it.</summary>
    internal const int MaxEntries = 10_000;

    private const int MaxTags = 200;
    private const int MaxBacklinks = 200;
    private const string SourceParameter = "$source";
    private const string ScopeFilter = "($sources IS NULL OR s.id IN (SELECT value FROM json_each($sources)))";
    private readonly IDatabase _database;

    public SqliteNoteBrowser(IDatabase database)
    {
        _database = database;
    }

    public NoteListing List(SourceScope scope, Guid? sourceId, string? tag)
    {
        using var connection = _database.Open();
        const string FromClause = $"""
            FROM notes n JOIN sources s ON s.id = n.source_id
            WHERE {ScopeFilter}
              AND ($source IS NULL OR s.id = $source)
              AND ($tag IS NULL OR EXISTS (SELECT 1 FROM note_tags t WHERE t.note_id = n.id AND t.tag = $tag))
            """;
        using var command = connection.CreateCommand();
        // The limit is per source: a large vault must not push the others out of the tree.
        command.CommandText = $"""
            SELECT id, name, path, title, topic FROM (
                SELECT s.id AS id, s.name AS name, n.path AS path, n.title AS title, n.topic AS topic,
                       ROW_NUMBER() OVER (PARTITION BY s.id ORDER BY n.path) AS position
                {FromClause}
            ) WHERE position <= $limit ORDER BY name, path
            """;
        BindListing(command, scope, sourceId, tag);
        command.Parameters.AddWithValue("$limit", MaxEntries);
        var entries = new List<NoteEntry>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                entries.Add(new NoteEntry(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }

        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) {FromClause}";
        BindListing(count, scope, sourceId, tag);
        return new NoteListing(entries, Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    public LinkAnswers Answer(Guid sourceId, string notePath, LinkQuestions questions, SourceScope scope)
    {
        using var connection = _database.Open();
        return new LinkAnswers(
            questions.Targets.ToDictionary(target => target, target => Resolve(connection, target, sourceId, scope), StringComparer.Ordinal),
            questions.Embeds.ToDictionary(embed => embed, embed => FindImage(connection, sourceId, notePath, embed), StringComparer.OrdinalIgnoreCase),
            questions.NotePaths.ToDictionary(path => path, path => ReadNote(connection, sourceId, path, scope)?.Reference, StringComparer.Ordinal),
            questions.ImagePaths.Where(path => HasImage(connection, sourceId, path)).ToHashSet(StringComparer.Ordinal));
    }

    public bool HasImage(Guid sourceId, string path)
    {
        using var connection = _database.Open();
        return HasImage(connection, sourceId, path);
    }

    private static bool HasImage(SqliteConnection connection, Guid sourceId, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM images WHERE source_id = $source AND path = $path";
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() is not null;
    }

    private static string? FindImage(SqliteConnection connection, Guid sourceId, string notePath, string embed)
    {
        var wanted = embed.Trim().Replace('\\', '/').TrimStart('/');
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM images WHERE source_id = $source AND name = $name";
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        command.Parameters.AddWithValue("$name", System.IO.Path.GetFileName(wanted).ToLowerInvariant());
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        var folder = notePath.Contains('/', StringComparison.Ordinal) ? notePath[..notePath.LastIndexOf('/')] + "/" : string.Empty;
        return paths
            .Where(path => string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase) || path.EndsWith('/' + wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Nearness(path, folder, wanted))
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>0 for the image next to the note, 1 for the one at the root of the source, 2 for any other.</summary>
    private static int Nearness(string path, string folder, string wanted)
    {
        if (string.Equals(path, folder + wanted, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static void BindListing(SqliteCommand command, SourceScope scope, Guid? sourceId, string? tag)
    {
        AddScope(command, scope);
        command.Parameters.AddWithValue(SourceParameter, sourceId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$tag", tag?.ToLowerInvariant() ?? (object)DBNull.Value);
    }

    public TagListing Tags(SourceScope scope)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.tag, COUNT(*) AS used
            FROM note_tags t JOIN notes n ON n.id = t.note_id JOIN sources s ON s.id = n.source_id
            WHERE {ScopeFilter}
            GROUP BY t.tag
            ORDER BY used DESC, t.tag
            """;
        AddScope(command, scope);
        using var reader = command.ExecuteReader();
        var tags = new List<TagCount>();
        var total = 0;
        while (reader.Read())
        {
            total++;
            if (tags.Count < MaxTags)
            {
                tags.Add(new TagCount(reader.GetString(0), reader.GetInt32(1)));
            }
        }

        return new TagListing(tags, total);
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
        var (backlinks, backlinksTotal) = Backlinks(connection, id, reference, name, pathWithoutExtension, scope);
        return new NoteDetail(
            reference,
            Column(connection, "SELECT DISTINCT tag FROM note_tags WHERE note_id = $note ORDER BY tag", id),
            [.. Column(connection, "SELECT DISTINCT target FROM note_links WHERE note_id = $note ORDER BY target", id).Select(target => new ResolvedLink(target, Resolve(connection, target, sourceId, scope)))],
            backlinks,
            backlinksTotal);
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
        command.Parameters.AddWithValue(SourceParameter, sourceId.ToString());
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), new NoteRef(sourceId, reader.GetString(1), reader.GetString(2), reader.GetString(3)), reader.GetString(4))
            : null;
    }

    // The notes that have the name a link ends with, on the indexed name; what is left of the link (a path) is compared in code, where the
    // case is folded the same way as when the index was written (SQLite only folds ASCII).
    private const string ResolveByName = $"""
        SELECT s.id, s.name, n.path, n.title
        FROM notes n JOIN sources s ON s.id = n.source_id
        WHERE {ScopeFilter} AND n.name = $name
        """;

    /// <summary>
    /// The note a link points at. A name (<c>[[docker]]</c>) is the name of a file anywhere in the sources, a path (<c>[[devops/docker]]</c>) is
    /// the end of a path. When several notes answer, the one in the same source wins, then the shortest path, then the source name: the
    /// answer is the same every time.
    /// </summary>
    private static NoteRef? Resolve(SqliteConnection connection, string target, Guid fromSource, SourceScope scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ResolveByName;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$name", target[(target.LastIndexOf('/') + 1)..]);
        using var reader = command.ExecuteReader();
        var candidates = new List<NoteRef>();
        while (reader.Read())
        {
            candidates.Add(new NoteRef(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return candidates
            .Where(note => LeadsTo(note.Path, target))
            .OrderByDescending(note => note.SourceId == fromSource)
            .ThenBy(note => note.Path.Length)
            .ThenBy(note => note.SourceName, StringComparer.Ordinal)
            .ThenBy(note => note.Path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Whether a link (lower case, no extension) points at the note at this path: it is the whole path or the end of it.</summary>
    private static bool LeadsTo(string path, string target)
    {
        var key = WithoutExtension(path).ToLowerInvariant();
        return !target.Contains('/', StringComparison.Ordinal) || key == target || key.EndsWith('/' + target, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notes whose links lead to this one. The index finds the notes that name it; each is kept only if its link, resolved the way
    /// <see cref="Resolve(SqliteConnection, string, Guid, SourceScope)"/> does for the note that holds it, really leads here: with two notes of the same name, a link leads to one, and
    /// only that one has it as a backlink.
    /// </summary>
    private static (List<NoteRef> Notes, int Total) Backlinks(SqliteConnection connection, long noteId, NoteRef self, string name, string pathWithoutExtension, SourceScope scope)
    {
        using var command = connection.CreateCommand();
        // The targets that can lead here are the name of the note and each end of its path (`c`, `b/c`, `a/b/c`): looked up on the index of targets.
        command.CommandText = $"""
            SELECT DISTINCT s.id, s.name, n.path, n.title, l.target
            FROM note_links l JOIN notes n ON n.id = l.note_id JOIN sources s ON s.id = n.source_id
            WHERE {ScopeFilter}
              AND n.id <> $note
              AND l.target IN (SELECT value FROM json_each($targets))
            ORDER BY s.name, n.path
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$note", noteId);
        command.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(TargetsThatLeadTo(name, pathWithoutExtension)));
        var candidates = new List<(NoteRef Note, string Target)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add((new NoteRef(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)), reader.GetString(4)));
            }
        }

        // Many notes link with the same words: each distinct link is resolved once, from the source of the note that holds it.
        var resolved = new Dictionary<(string Target, Guid From), NoteRef?>();
        var all = candidates
            .Where(candidate =>
            {
                var key = (candidate.Target, candidate.Note.SourceId);
                if (!resolved.TryGetValue(key, out var found))
                {
                    found = Resolve(connection, candidate.Target, candidate.Note.SourceId, scope);
                    resolved[key] = found;
                }

                return found is not null && found.SourceId == self.SourceId && found.Path == self.Path;
            })
            .Select(candidate => candidate.Note)
            .DistinctBy(note => (note.SourceId, note.Path))
            .ToList();
        return ([.. all.Take(MaxBacklinks)], all.Count);
    }

    private static IEnumerable<string> TargetsThatLeadTo(string name, string pathWithoutExtension)
    {
        yield return name;
        var parts = pathWithoutExtension.Split('/');
        for (var from = 0; from < parts.Length - 1; from++)
        {
            yield return string.Join('/', parts[from..]);
        }
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
