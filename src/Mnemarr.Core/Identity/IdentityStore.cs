using System.Globalization;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Identity;

/// <summary>What an administrator set in the interface: each part is null while it is not set (the deployment's settings, then the product's own, apply). The content of the logo is read apart.</summary>
public sealed record StoredIdentity(string? Name, string? NameColor, string? VersionColor, string? LogoType, string? LogoHash, DateTimeOffset? LogoAt);

public interface IIdentityStore
{
    /// <summary>The name, the colors and what is known of the logo, without its content.</summary>
    StoredIdentity Get();

    /// <summary>The content of the logo, or null when there is none.</summary>
    byte[]? GetLogo();

    void SaveTexts(string? name, string? nameColor, string? versionColor);

    void SaveLogo(byte[] content, string type, string hash, DateTimeOffset at);

    void ClearLogo();
}

/// <summary>The identity is one row (<c>identity</c>, id 1), in the same database as the rest: it is backed up with it. A write touches its own columns only.</summary>
public sealed class SqliteIdentityStore : IIdentityStore
{
    private readonly IDatabase _database;

    public SqliteIdentityStore(IDatabase database)
    {
        _database = database;
    }

    public StoredIdentity Get()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, name_color, version_color, logo_type, logo_hash, logo_at FROM identity WHERE id = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new StoredIdentity(null, null, null, null, null, null);
        }

        return new StoredIdentity(
            Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4),
            Text(reader, 5) is { } at ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture) : null);
    }

    public byte[]? GetLogo()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT logo FROM identity WHERE id = 1";
        return command.ExecuteScalar() as byte[];
    }

    public void SaveTexts(string? name, string? nameColor, string? versionColor) =>
        Write("UPDATE identity SET name = $name, name_color = $nameColor, version_color = $versionColor WHERE id = 1", command =>
        {
            command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            command.Parameters.AddWithValue("$nameColor", (object?)nameColor ?? DBNull.Value);
            command.Parameters.AddWithValue("$versionColor", (object?)versionColor ?? DBNull.Value);
        });

    public void SaveLogo(byte[] content, string type, string hash, DateTimeOffset at) =>
        Write("UPDATE identity SET logo = $logo, logo_type = $type, logo_hash = $hash, logo_at = $at WHERE id = 1", command =>
        {
            command.Parameters.AddWithValue("$logo", content);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        });

    public void ClearLogo() => Write("UPDATE identity SET logo = NULL, logo_type = NULL, logo_hash = NULL, logo_at = NULL WHERE id = 1", _ => { });

    /// <summary>Makes sure the row exists, then changes the columns it is given and no other: two changes that do not touch the same part do not undo each other.</summary>
    private void Write(string update, Action<SqliteCommand> bind)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText = "INSERT OR IGNORE INTO identity (id) VALUES (1)";
            ensure.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = update;
        bind(command);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
}
