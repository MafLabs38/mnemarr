using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Sources;

public sealed class SqliteSourceStore : ISourceStore
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE: the extended code, so that another constraint is not reported as a name already taken.</summary>
    private const int UniqueViolation = 2067;
    private readonly IDatabase _database;

    public SqliteSourceStore(IDatabase database)
    {
        _database = database;
    }

    public IReadOnlyList<Source> List()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, kind, mode, path, created_at, managed, repository_url, repository_branch, repository_credential FROM sources ORDER BY name";
        using var reader = command.ExecuteReader();
        var sources = new List<Source>();
        while (reader.Read())
        {
            sources.Add(Read(reader));
        }

        return sources;
    }

    public Source? Get(Guid id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, kind, mode, path, created_at, managed, repository_url, repository_branch, repository_credential FROM sources WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Add(Source source)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sources (id, name, kind, mode, path, created_at, managed, repository_url, repository_branch, repository_credential) VALUES ($id, $name, $kind, $mode, $path, $at, $managed, $url, $branch, $credential)";
        command.Parameters.AddWithValue("$id", source.Id.ToString());
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$kind", source.Kind.ToString());
        command.Parameters.AddWithValue("$mode", source.Mode.ToString());
        command.Parameters.AddWithValue("$path", source.Path);
        command.Parameters.AddWithValue("$at", StorageText.Format(source.CreatedAt));
        command.Parameters.AddWithValue("$managed", source.Managed ? 1 : 0);
        BindRepository(command, source);
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == UniqueViolation)
        {
            throw new RejectedException(Problem.Conflict, $"A source named '{source.Name}' already exists.");
        }
    }

    public bool Update(Source source)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sources SET name = $name, mode = $mode, path = $path, managed = $managed, repository_url = $url, repository_branch = $branch, repository_credential = $credential WHERE id = $id";
        command.Parameters.AddWithValue("$id", source.Id.ToString());
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$mode", source.Mode.ToString());
        command.Parameters.AddWithValue("$path", source.Path);
        command.Parameters.AddWithValue("$managed", source.Managed ? 1 : 0);
        BindRepository(command, source);
        try
        {
            return command.ExecuteNonQuery() > 0;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == UniqueViolation)
        {
            throw new RejectedException(Problem.Conflict, $"A source named '{source.Name}' already exists.");
        }
    }

    public bool Remove(Guid id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sources WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteNonQuery() > 0;
    }

    private static void BindRepository(SqliteCommand command, Source source)
    {
        command.Parameters.AddWithValue("$url", source.Repository?.Url ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$branch", source.Repository?.Branch ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$credential", source.Repository?.Credential ?? (object)DBNull.Value);
    }

    private static GitRepository? ReadRepository(SqliteDataReader reader)
    {
        if (reader.IsDBNull(7))
        {
            return null;
        }

        var branch = reader.IsDBNull(8) ? null : reader.GetString(8);
        var credential = reader.IsDBNull(9) ? null : reader.GetString(9);
        return new GitRepository(reader.GetString(7), branch, credential);
    }

    private static Source Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        Enum.Parse<SourceKind>(reader.GetString(2)),
        Enum.Parse<SourceMode>(reader.GetString(3)),
        reader.GetString(4),
        StorageText.Parse(reader.GetString(5)),
        reader.GetInt32(6) != 0,
        ReadRepository(reader));
}
