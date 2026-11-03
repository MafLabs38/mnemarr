using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Accounts;

public sealed class SqliteApiKeyStore : IApiKeyStore
{
    private const string Columns = "id, account_id, name, prefix, sources, created_at, expires_at, last_used_at, revoked_at";
    private const string IdParameter = "$id";
    private const string AccountParameter = "$account";
    private readonly IDatabase _database;

    public SqliteApiKeyStore(IDatabase database)
    {
        _database = database;
    }

    public void Add(ApiKey key, string secretHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO api_keys (id, account_id, name, prefix, secret_hash, sources, created_at, expires_at) VALUES ($id, $account, $name, $prefix, $hash, $sources, $at, $expires)";
        command.Parameters.AddWithValue(IdParameter, key.Id.ToString());
        command.Parameters.AddWithValue(AccountParameter, key.AccountId.ToString());
        command.Parameters.AddWithValue("$name", key.Name);
        command.Parameters.AddWithValue("$prefix", key.Prefix);
        command.Parameters.AddWithValue("$hash", secretHash);
        command.Parameters.AddWithValue("$sources", key.Sources is null ? DBNull.Value : JsonSerializer.Serialize(key.Sources));
        command.Parameters.AddWithValue("$at", StorageText.Format(key.CreatedAt));
        command.Parameters.AddWithValue("$expires", key.ExpiresAt is { } expires ? StorageText.Format(expires) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public ApiKey? FindByHash(string secretHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM api_keys WHERE secret_hash = $hash";
        command.Parameters.AddWithValue("$hash", secretHash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<ApiKey> List(Guid accountId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM api_keys WHERE account_id = $account ORDER BY created_at";
        command.Parameters.AddWithValue(AccountParameter, accountId.ToString());
        using var reader = command.ExecuteReader();
        var keys = new List<ApiKey>();
        while (reader.Read())
        {
            keys.Add(Read(reader));
        }

        return keys;
    }

    public bool Revoke(Guid accountId, Guid keyId, DateTimeOffset at)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE api_keys SET revoked_at = $at WHERE id = $id AND account_id = $account AND revoked_at IS NULL";
        command.Parameters.AddWithValue(IdParameter, keyId.ToString());
        command.Parameters.AddWithValue(AccountParameter, accountId.ToString());
        command.Parameters.AddWithValue("$at", StorageText.Format(at));
        return command.ExecuteNonQuery() > 0;
    }

    public void Touch(Guid keyId, DateTimeOffset at)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE api_keys SET last_used_at = $at WHERE id = $id";
        command.Parameters.AddWithValue(IdParameter, keyId.ToString());
        command.Parameters.AddWithValue("$at", StorageText.Format(at));
        command.ExecuteNonQuery();
    }


    private static DateTimeOffset? Date(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : StorageText.Parse(reader.GetString(column));

    private static ApiKey Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<List<Guid>>(reader.GetString(4)),
        StorageText.Parse(reader.GetString(5)),
        Date(reader, 6),
        Date(reader, 7),
        Date(reader, 8));
}
