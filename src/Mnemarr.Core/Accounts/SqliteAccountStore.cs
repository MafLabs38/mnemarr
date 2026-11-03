using System.Globalization;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Accounts;

public sealed class SqliteAccountStore : IAccountStore
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE: the extended code, so that another constraint is not reported as a name already taken.</summary>
    private const int UniqueViolation = 2067;
    private const string Columns = "id, name, kind, role, enabled, created_at";
    private const string IdParameter = "$id";
    private readonly IDatabase _database;

    public SqliteAccountStore(IDatabase database)
    {
        _database = database;
    }

    public void Add(Account account, string? passwordHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO accounts (id, name, kind, role, enabled, password_hash, created_at) VALUES ($id, $name, $kind, $role, $enabled, $hash, $at)";
        command.Parameters.AddWithValue(IdParameter, account.Id.ToString());
        command.Parameters.AddWithValue("$name", account.Name);
        command.Parameters.AddWithValue("$kind", account.Kind.ToString());
        command.Parameters.AddWithValue("$role", account.Role);
        command.Parameters.AddWithValue("$enabled", account.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$hash", passwordHash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$at", StorageText.Format(account.CreatedAt));
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == UniqueViolation)
        {
            throw new AccountRejectedException(AccountProblem.Conflict, $"An account named '{account.Name}' already exists.");
        }
    }

    public Account? Get(Guid id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM accounts WHERE id = $id";
        command.Parameters.AddWithValue(IdParameter, id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public AccountCredentials? GetCredentials(string name)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns}, password_hash FROM accounts WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var hash = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new AccountCredentials(Read(reader), hash);
    }

    public IReadOnlyList<Account> List()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM accounts ORDER BY name";
        using var reader = command.ExecuteReader();
        var accounts = new List<Account>();
        while (reader.Read())
        {
            accounts.Add(Read(reader));
        }

        return accounts;
    }

    public bool Update(Guid id, string role, bool enabled)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE accounts SET role = $role, enabled = $enabled WHERE id = $id";
        command.Parameters.AddWithValue(IdParameter, id.ToString());
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetPasswordHash(Guid id, string passwordHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE accounts SET password_hash = $hash WHERE id = $id AND kind = $user";
        command.Parameters.AddWithValue(IdParameter, id.ToString());
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$user", nameof(AccountKind.User));
        return command.ExecuteNonQuery() > 0;
    }

    public bool Remove(Guid id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM accounts WHERE id = $id";
        command.Parameters.AddWithValue(IdParameter, id.ToString());
        return command.ExecuteNonQuery() > 0;
    }

    public int Count() => Scalar("SELECT COUNT(*) FROM accounts");

    public int CountEnabledAdmins()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM accounts WHERE enabled = 1 AND role = $admin";
        command.Parameters.AddWithValue("$admin", Roles.Admin);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyDictionary<string, int> CountByRole()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role, COUNT(*) FROM accounts GROUP BY role";
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    private int Scalar(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static Account Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        Enum.Parse<AccountKind>(reader.GetString(2)),
        reader.GetString(3),
        reader.GetInt32(4) == 1,
        StorageText.Parse(reader.GetString(5)));
}
