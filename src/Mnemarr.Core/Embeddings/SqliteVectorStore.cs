using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
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
    private bool? _available;
    private string? _reason;

    public SqliteVectorStore(IDatabase database, IOptions<EmbeddingOptions> options)
    {
        _database = database;
        _options = options.Value;
    }

    public bool TryPrepare(out string? reason)
    {
        lock (_gate)
        {
            if (_available is null)
            {
                try
                {
                    using var connection = OpenWithVectors();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT vec_version()";
                    _ = command.ExecuteScalar();
                    _available = true;
                }
                catch (Exception ex) when (ex is SqliteException or ArgumentException or InvalidOperationException or IOException)
                {
                    _available = false;
                    _reason = $"The vector library could not be loaded: {ex.Message}";
                }
            }

            reason = _reason;
            return _available == true;
        }
    }

    public IReadOnlyList<ChunkToEmbed> Missing(string model, int limit)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, n.title, c.heading, c.text
            FROM chunks c
            JOIN notes n ON n.id = c.note_id
            LEFT JOIN chunk_embeddings e ON e.chunk_id = c.id
            WHERE e.chunk_id IS NULL OR e.model <> $model
            ORDER BY c.id
            LIMIT $limit
            """;
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

    public void Save(string model, int dimension, IReadOnlyList<(long ChunkId, float[] Vector)> items)
    {
        using var connection = OpenWithVectors();
        var table = TableOf(model, dimension);
        Create(connection, table, dimension);
        using var transaction = connection.BeginTransaction();
        foreach (var (chunkId, vector) in items)
        {
            if (!MarkEmbedded(connection, transaction, chunkId, model))
            {
                continue;
            }

            Execute(connection, transaction, $"DELETE FROM {table} WHERE rowid = $id", ("$id", chunkId));
            Execute(connection, transaction, $"INSERT INTO {table} (rowid, embedding) VALUES ($id, $vector)", ("$id", chunkId), ("$vector", MemoryMarshal.AsBytes(vector.AsSpan()).ToArray()));
        }

        transaction.Commit();
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

        return matches;
    }

    public EmbeddingProgress Progress(string model)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM chunks), (SELECT COUNT(*) FROM chunk_embeddings WHERE model = $model)";
        command.Parameters.AddWithValue(ModelParameter, model);
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

        foreach (var id in stale)
        {
            Execute(connection, null, $"DELETE FROM {table} WHERE rowid = $id", ("$id", id));
        }
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
