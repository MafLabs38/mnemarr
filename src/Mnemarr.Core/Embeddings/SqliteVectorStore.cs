using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Embeddings;

/// <summary>
/// The vectors, in the database, with the <c>sqlite-vec</c> extension (ADR 0012). The library is loaded by this code from the path it was given by its operator or its
/// image, and loading is switched off on the connection straight after: nothing that reaches SQL can load another. Every value of a search is a bound parameter,
/// and the name of a table is made of a hash, never of input.
/// </summary>
public sealed class SqliteVectorStore : IVectorStore
{
    private const string EntryPoint = "sqlite3_vec_init";
    private const string ModelParameter = "$model";

    private readonly IDatabase _database;
    private readonly EmbeddingOptions _options;
    private readonly Lock _gate = new();
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock;
    private bool? _available;
    private string? _reason;
    private DateTimeOffset _checkedAt;

    public SqliteVectorStore(IDatabase database, IOptions<EmbeddingOptions> options, TimeProvider? clock = null)
    {
        _database = database;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public bool TryPrepare(out string? reason)
    {
        lock (_gate)
        {
            // A library that could not be loaded is tried again after a while: a file that was put back, a volume that was mounted late.
            if (_available is null || (_available == false && _clock.GetUtcNow() - _checkedAt >= RetryAfter))
            {
                _checkedAt = _clock.GetUtcNow();
                try
                {
                    using var connection = OpenWithVectors();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT vec_version()";
                    _ = command.ExecuteScalar();
                    _available = true;
                    _reason = null;
                }
                catch (Exception ex) when (ex is SqliteException or ArgumentException or InvalidOperationException or IOException)
                {
                    _available = false;
                    // The detail (the path, the loader's words) is not given to the people who ask where the search is at.
                    _reason = "The vector library could not be loaded: its path or its file is wrong.";
                }
            }

            reason = _reason;
            return _available == true;
        }
    }

    public IReadOnlyList<ChunkToEmbed> Missing(string model, int dimension, int limit, IReadOnlySet<long> skipped)
    {
        // A chunk whose text was computed before is given its vector here, and the next ones are looked at: the caller only gets what the model has to read. The chunks that were
        // given a vector are left out of the next look, so that the loop always moves on.
        var left = skipped;
        while (true)
        {
            var candidates = Candidates(model, limit, left);
            if (candidates.Count == 0)
            {
                return candidates;
            }

            var (needed, given) = GiveKnownVectors(model, dimension, candidates);
            if (needed.Count > 0)
            {
                return needed;
            }

            left = new HashSet<long>(left.Concat(given));
        }
    }

    private List<ChunkToEmbed> Candidates(string model, int limit, IReadOnlySet<long> skipped)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, n.title, c.heading, c.text
            FROM chunks c
            JOIN notes n ON n.id = c.note_id
            LEFT JOIN chunk_embeddings e ON e.chunk_id = c.id
            WHERE (e.chunk_id IS NULL OR e.model <> $model) AND c.id NOT IN (SELECT value FROM json_each($skipped))
            ORDER BY c.id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$skipped", System.Text.Json.JsonSerializer.Serialize(skipped));
        command.Parameters.AddWithValue(ModelParameter, model);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var chunks = new List<ChunkToEmbed>();
        while (reader.Read())
        {
            // The title and the heading say what the passage is about, which the passage alone may not.
            var heading = reader.GetString(2);
            var text = string.IsNullOrEmpty(heading) ? $"{reader.GetString(1)}\n{reader.GetString(3)}" : $"{reader.GetString(1)} — {heading}\n{reader.GetString(3)}";
            chunks.Add(new ChunkToEmbed(reader.GetInt64(0), text));
        }

        return chunks;
    }

    // The chunks of the list whose text has a vector already (kept under the hash of the text, ADR 0039) are given it; the others are returned, and so are the ids that were given.
    // Of several chunks that share a text, the model is asked for one: the others are given its vector at the next look.
    private (List<ChunkToEmbed> Needed, List<long> Given) GiveKnownVectors(string model, int dimension, List<ChunkToEmbed> candidates)
    {
        var known = KnownVectors(model, dimension, candidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var needed = candidates.Where(chunk => !known.ContainsKey(chunk.Hash) && seen.Add(chunk.Hash)).ToList();
        var reused = candidates.Where(chunk => known.ContainsKey(chunk.Hash)).ToList();
        if (reused.Count == 0)
        {
            return (needed, []);
        }

        using var connection = OpenWithVectors();
        var table = TableOf(model, dimension);
        Create(connection, table, dimension);
        using var transaction = connection.BeginTransaction();
        foreach (var chunk in reused)
        {
            KeepVector(connection, transaction, table, model, chunk.ChunkId, known[chunk.Hash]);
        }

        Execute(connection, transaction, "UPDATE embedding_cache SET used_at = $at WHERE model = $model AND text_hash IN (SELECT value FROM json_each($hashes))",
            ("$at", StorageText.Format(_clock.GetUtcNow())), (ModelParameter, model), ("$hashes", System.Text.Json.JsonSerializer.Serialize(known.Keys)));
        transaction.Commit();
        return (needed, [.. reused.Select(chunk => chunk.ChunkId)]);
    }

    private Dictionary<string, byte[]> KnownVectors(string model, int dimension, List<ChunkToEmbed> candidates)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text_hash, vector FROM embedding_cache WHERE model = $model AND text_hash IN (SELECT value FROM json_each($hashes))";
        command.Parameters.AddWithValue(ModelParameter, model);
        command.Parameters.AddWithValue("$hashes", System.Text.Json.JsonSerializer.Serialize(candidates.Select(chunk => chunk.Hash).Distinct()));
        using var reader = command.ExecuteReader();
        var known = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.Read())
        {
            // A vector of another size is not this model's: it is computed again.
            if (reader.GetFieldValue<byte[]>(1) is { } vector && vector.Length == dimension * sizeof(float))
            {
                known[reader.GetString(0)] = vector;
            }
        }

        return known;
    }

    public void Save(string model, int dimension, IReadOnlyList<ComputedVector> items)
    {
        using var connection = OpenWithVectors();
        var table = TableOf(model, dimension);
        Create(connection, table, dimension);
        using var transaction = connection.BeginTransaction();
        var at = StorageText.Format(_clock.GetUtcNow());
        foreach (var (chunkId, hash, vector) in items)
        {
            var bytes = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
            KeepVector(connection, transaction, table, model, chunkId, bytes);

            // Kept even when the chunk was replaced meanwhile: its text is most likely back in a chunk of the new note, and the model has read it.
            Execute(connection, transaction, "INSERT OR REPLACE INTO embedding_cache (model, text_hash, vector, used_at) VALUES ($model, $hash, $vector, $at)",
                (ModelParameter, model), ("$hash", hash), ("$vector", bytes), ("$at", at));
        }

        transaction.Commit();
    }

    // The vector of a chunk, in the table of the model and in the bookkeeping, unless the chunk was replaced meanwhile.
    private static void KeepVector(SqliteConnection connection, SqliteTransaction transaction, string table, string model, long chunkId, byte[] vector)
    {
        if (!MarkEmbedded(connection, transaction, chunkId, model))
        {
            return;
        }

        Execute(connection, transaction, $"DELETE FROM {table} WHERE rowid = $id", ("$id", chunkId));
        Execute(connection, transaction, $"INSERT INTO {table} (rowid, embedding) VALUES ($id, $vector)", ("$id", chunkId), ("$vector", vector));
    }

    public IReadOnlyList<VectorMatch> Nearest(string model, int dimension, float[] query, int k)
    {
        using var connection = OpenWithVectors();
        var table = TableOf(model, dimension);
        if (!Exists(connection, table))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        // S2077: the table is a name made of a hash by TableOf, and a virtual table cannot be a parameter; every value is bound.
#pragma warning disable S2077
        command.CommandText = $"SELECT rowid, distance FROM {table} WHERE embedding MATCH $query AND k = $k ORDER BY distance";
#pragma warning restore S2077
        command.Parameters.AddWithValue("$query", MemoryMarshal.AsBytes(query.AsSpan()).ToArray());
        command.Parameters.AddWithValue("$k", k);
        using var reader = command.ExecuteReader();
        var matches = new List<VectorMatch>();
        while (reader.Read())
        {
            matches.Add(new VectorMatch(reader.GetInt64(0), reader.GetDouble(1)));
        }

        return CurrentOnly(connection, matches, model);
    }

    // A row of the table can outlive its chunk (a chunk is replaced when its note changes, and the cleanup comes after) and an id can be given again to another chunk:
    // only the ones that the bookkeeping says are current for the model are matches.
    private static List<VectorMatch> CurrentOnly(SqliteConnection connection, List<VectorMatch> matches, string model)
    {
        if (matches.Count == 0)
        {
            return matches;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk_id FROM chunk_embeddings WHERE model = $model AND chunk_id IN (SELECT value FROM json_each($ids))";
        command.Parameters.AddWithValue(ModelParameter, model);
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(matches.Select(match => match.ChunkId)));
        using var reader = command.ExecuteReader();
        var current = new HashSet<long>();
        while (reader.Read())
        {
            current.Add(reader.GetInt64(0));
        }

        return [.. matches.Where(match => current.Contains(match.ChunkId))];
    }

    public EmbeddingProgress Progress(string model, SourceScope? scope = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        var restricted = scope is { } given && (given.Sources is not null || given.ConversationsHidden) ? given : null;
        if (restricted is null)
        {
            // Everything, for the probes of the host and for a caller who may see it all: two counts that read an index each, not a join over every chunk.
            command.CommandText = "SELECT (SELECT COUNT(*) FROM chunks), (SELECT COUNT(*) FROM chunk_embeddings WHERE model = $model)";
            command.Parameters.AddWithValue(ModelParameter, model);
            using var all = command.ExecuteReader();
            all.Read();
            return new EmbeddingProgress(all.GetInt32(0), all.GetInt32(1));
        }

        // Counted among the chunks of the sources the caller may see: the totals of the whole database tell a caller how much the others are doing.
        command.CommandText = """
            SELECT COUNT(*), COUNT(e.chunk_id)
            FROM chunks c
            JOIN notes n ON n.id = c.note_id
            JOIN sources s ON s.id = n.source_id
            LEFT JOIN chunk_embeddings e ON e.chunk_id = c.id AND e.model = $model
            WHERE ($sources IS NULL OR s.id IN (SELECT value FROM json_each($sources))) AND ($hideConversations = 0 OR s.kind <> $conversationsKind)
            """;
        command.Parameters.AddWithValue(ModelParameter, model);
        command.Parameters.AddWithValue("$sources", restricted.Sources is { } allowed ? System.Text.Json.JsonSerializer.Serialize(allowed.Select(id => id.ToString())) : DBNull.Value);
        command.Parameters.AddWithValue("$hideConversations", restricted.ConversationsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$conversationsKind", nameof(Mnemarr.Core.Sources.SourceKind.Conversations));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new EmbeddingProgress(reader.GetInt32(0), reader.GetInt32(1));
    }

    public void Tidy(string model, int dimension)
    {
        using var connection = OpenWithVectors();
        var active = TableOf(model, dimension);
        if (Exists(connection, active))
        {
            RemoveStaleRows(connection, active, model);
        }

        // The vectors that were computed and have not been used for a while are forgotten (ADR 0039).
        Execute(connection, null, "DELETE FROM embedding_cache WHERE used_at < $before", ("$before", StorageText.Format(_clock.GetUtcNow().AddDays(-_options.CacheDays))));

        // Tables of other models go once no chunk has a vector of another model: until then they still answer for the chunks not yet computed again.
        using var others = connection.CreateCommand();
        others.CommandText = "SELECT EXISTS (SELECT 1 FROM chunk_embeddings WHERE model <> $model)";
        others.Parameters.AddWithValue(ModelParameter, model);
        if (Convert.ToInt32(others.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            foreach (var name in TablesOtherThan(connection, active))
            {
                Execute(connection, null, $"DROP TABLE {name}");
            }

            Execute(connection, null, "DELETE FROM embedding_cache WHERE model <> $model", (ModelParameter, model));
        }
    }

    private static string TableOf(string model, int dimension) =>
        "vec_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{model}|{dimension}")))[..16];

    private static void Create(SqliteConnection connection, string table, int dimension) =>
        Execute(connection, null, $"CREATE VIRTUAL TABLE IF NOT EXISTS {table} USING vec0(embedding float[{dimension.ToString(System.Globalization.CultureInfo.InvariantCulture)}] distance_metric=cosine)");

    private static bool Exists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name)";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static List<string> TablesOtherThan(SqliteConnection connection, string active)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'vec\\_%' ESCAPE '\\' AND sql LIKE 'CREATE VIRTUAL TABLE%' AND name <> $active";
        command.Parameters.AddWithValue("$active", active);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static bool MarkEmbedded(SqliteConnection connection, SqliteTransaction transaction, long chunkId, string model)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO chunk_embeddings (chunk_id, model) SELECT $id, $model WHERE EXISTS (SELECT 1 FROM chunks WHERE id = $id)";
        command.Parameters.AddWithValue("$id", chunkId);
        command.Parameters.AddWithValue(ModelParameter, model);
        return command.ExecuteNonQuery() == 1;
    }

    private static void RemoveStaleRows(SqliteConnection connection, string table, string model)
    {
        var kept = new HashSet<long>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT chunk_id FROM chunk_embeddings WHERE model = $model";
            command.Parameters.AddWithValue(ModelParameter, model);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                kept.Add(reader.GetInt64(0));
            }
        }

        var stale = new List<long>();
        using (var command = connection.CreateCommand())
        {
            // S2077: the table is a name made of a hash by TableOf, never input.
#pragma warning disable S2077
            command.CommandText = $"SELECT rowid FROM {table}";
#pragma warning restore S2077
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!kept.Contains(reader.GetInt64(0)))
                {
                    stale.Add(reader.GetInt64(0));
                }
            }
        }

        // One transaction: after a model change or a big re-index they are thousands, and one commit for each is one write for each on the only writer.
        using var transaction = connection.BeginTransaction();
        foreach (var id in stale)
        {
            Execute(connection, transaction, $"DELETE FROM {table} WHERE rowid = $id", ("$id", id));
        }

        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenWithVectors()
    {
        var connection = _database.Open();
        try
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(_options.ExtensionPath!, EntryPoint);
            connection.EnableExtensions(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
