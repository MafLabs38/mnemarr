using System.Globalization;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Accounts;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly IDatabase _database;

    public SqliteSessionStore(IDatabase database)
    {
        _database = database;
    }

    public void Add(string id, Guid accountId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sessions (id, account_id, created_at, expires_at) VALUES ($id, $account, $created, $expires)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", accountId.ToString());
        command.Parameters.AddWithValue("$created", Text(createdAt));
        command.Parameters.AddWithValue("$expires", Text(expiresAt));
        command.ExecuteNonQuery();
    }

    public Guid? AccountOf(string id, DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id, expires_at FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture) <= now)
        {
            return null;
        }

        return Guid.Parse(reader.GetString(0));
    }

    public void Remove(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void RemoveAll(Guid accountId, string? except)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE account_id = $account AND ($except IS NULL OR id <> $except)";
        command.Parameters.AddWithValue("$account", accountId.ToString());
        command.Parameters.AddWithValue("$except", except ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void RemoveExpired(DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE expires_at <= $now";
        command.Parameters.AddWithValue("$now", Text(now));
        command.ExecuteNonQuery();
    }

    private static string Text(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
}
