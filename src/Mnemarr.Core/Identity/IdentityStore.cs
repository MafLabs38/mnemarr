using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Identity;

/// <summary>What an administrator set in the interface: each part is null while it is not set (the deployment's settings, then the product's own, apply).</summary>
public sealed record StoredIdentity(string? Name, string? NameColor, string? VersionColor, byte[]? Logo, string? LogoType, string? LogoHash, DateTimeOffset? LogoAt);

public interface IIdentityStore
{
    StoredIdentity Get();

    void Save(StoredIdentity identity);
}

/// <summary>The identity is one row (<c>identity</c>, id 1), in the same database as the rest: it is backed up with it.</summary>
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
        command.CommandText = "SELECT name, name_color, version_color, logo, logo_type, logo_hash, logo_at FROM identity WHERE id = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new StoredIdentity(null, null, null, null, null, null, null);
        }

        return new StoredIdentity(
            Text(reader, 0), Text(reader, 1), Text(reader, 2), reader.IsDBNull(3) ? null : (byte[])reader[3], Text(reader, 4), Text(reader, 5),
            Text(reader, 6) is { } at ? DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    public void Save(StoredIdentity identity)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO identity (id, name, name_color, version_color, logo, logo_type, logo_hash, logo_at)
            VALUES (1, $name, $nameColor, $versionColor, $logo, $logoType, $logoHash, $logoAt)
            ON CONFLICT (id) DO UPDATE SET name = $name, name_color = $nameColor, version_color = $versionColor, logo = $logo, logo_type = $logoType, logo_hash = $logoHash, logo_at = $logoAt
            """;
        command.Parameters.AddWithValue("$name", (object?)identity.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$nameColor", (object?)identity.NameColor ?? DBNull.Value);
        command.Parameters.AddWithValue("$versionColor", (object?)identity.VersionColor ?? DBNull.Value);
        command.Parameters.AddWithValue("$logo", (object?)identity.Logo ?? DBNull.Value);
        command.Parameters.AddWithValue("$logoType", (object?)identity.LogoType ?? DBNull.Value);
        command.Parameters.AddWithValue("$logoHash", (object?)identity.LogoHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$logoAt", identity.LogoAt is { } at ? at.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
}
