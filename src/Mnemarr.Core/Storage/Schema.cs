using Microsoft.Data.Sqlite;

namespace Mnemarr.Core.Storage;

/// <summary>The schema, versioned with <c>PRAGMA user_version</c>: a step is applied once, in order.</summary>
internal static class Schema
{
    private static readonly string[] Steps =
    [
        // 1: accounts, their API keys, and the audit log.
        """
        CREATE TABLE accounts (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            kind TEXT NOT NULL,
            role TEXT NOT NULL,
            enabled INTEGER NOT NULL,
            password_hash TEXT,
            created_at TEXT NOT NULL
        );
        CREATE TABLE api_keys (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            prefix TEXT NOT NULL,
            secret_hash TEXT NOT NULL UNIQUE,
            sources TEXT,
            created_at TEXT NOT NULL,
            expires_at TEXT,
            last_used_at TEXT,
            revoked_at TEXT
        );
        CREATE INDEX api_keys_by_account ON api_keys (account_id);
        CREATE TABLE audit_events (
            id INTEGER PRIMARY KEY,
            at TEXT NOT NULL,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            target TEXT NOT NULL,
            detail TEXT NOT NULL
        );
        CREATE INDEX audit_events_by_action ON audit_events (action, id);
        """,
    ];

    public static void Apply(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);
        for (var step = version; step < Steps.Length; step++)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, Steps[step], transaction);
            Execute(connection, $"PRAGMA user_version = {step + 1};", transaction);
            transaction.Commit();
        }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
