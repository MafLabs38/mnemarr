using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Collectors;

public interface IPairingStore
{
    void Add(Pairing pairing, string userCodeHash, string deviceHash);

    Pairing? Get(Guid id);

    Pairing? FindByUserCodeHash(string hash);

    Pairing? FindByDeviceHash(string hash);

    /// <summary>The requests nobody has answered and whose time is not over, newest first.</summary>
    IReadOnlyList<Pairing> Open(DateTimeOffset now, int limit);

    int CountOpen(DateTimeOffset now);

    bool Approve(Guid id, string machine, MachineKind kind, string approvedBy);

    bool Reject(Guid id);

    bool Use(Guid id);
}

public sealed class SqlitePairingStore : IPairingStore
{
    private const string Columns = "id, hostname, state, machine, kind, requested_at, expires_at, approved_by";
    private readonly IDatabase _database;

    public SqlitePairingStore(IDatabase database)
    {
        _database = database;
    }

    public void Add(Pairing pairing, string userCodeHash, string deviceHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pairings (id, hostname, state, user_code_hash, device_hash, requested_at, expires_at)
            VALUES ($id, $hostname, $state, $user, $device, $requested, $expires)
            """;
        command.Parameters.AddWithValue("$id", pairing.Id.ToString());
        command.Parameters.AddWithValue("$hostname", pairing.Hostname);
        command.Parameters.AddWithValue("$state", pairing.State.ToString());
        command.Parameters.AddWithValue("$user", userCodeHash);
        command.Parameters.AddWithValue("$device", deviceHash);
        command.Parameters.AddWithValue("$requested", StorageText.Format(pairing.RequestedAt));
        command.Parameters.AddWithValue("$expires", StorageText.Format(pairing.ExpiresAt));
        command.ExecuteNonQuery();
    }

    public Pairing? Get(Guid id) => One($"SELECT {Columns} FROM pairings WHERE id = $key", id.ToString());

    public Pairing? FindByUserCodeHash(string hash) => One($"SELECT {Columns} FROM pairings WHERE user_code_hash = $key", hash);

    public Pairing? FindByDeviceHash(string hash) => One($"SELECT {Columns} FROM pairings WHERE device_hash = $key", hash);

    public IReadOnlyList<Pairing> Open(DateTimeOffset now, int limit)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM pairings WHERE state = 'Pending' AND expires_at > $now ORDER BY requested_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$now", StorageText.Format(now));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var found = new List<Pairing>();
        while (reader.Read())
        {
            found.Add(Read(reader));
        }

        return found;
    }

    public int CountOpen(DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pairings WHERE state = 'Pending' AND expires_at > $now";
        command.Parameters.AddWithValue("$now", StorageText.Format(now));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool Approve(Guid id, string machine, MachineKind kind, string approvedBy)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE pairings SET state = 'Approved', machine = $machine, kind = $kind, approved_by = $by WHERE id = $id AND state = 'Pending'";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$machine", machine);
        command.Parameters.AddWithValue("$kind", MachineKinds.Name(kind));
        command.Parameters.AddWithValue("$by", approvedBy);
        return command.ExecuteNonQuery() > 0;
    }

    public bool Reject(Guid id) => Change("UPDATE pairings SET state = 'Rejected' WHERE id = $id AND state = 'Pending'", id);

    public bool Use(Guid id) => Change("UPDATE pairings SET state = 'Used' WHERE id = $id AND state = 'Approved'", id);

    private bool Change(string update, Guid id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = update;
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteNonQuery() > 0;
    }

    private Pairing? One(string query, string key)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static Pairing Read(SqliteDataReader reader)
    {
        MachineKind? kind = null;
        if (!reader.IsDBNull(4))
        {
            kind = MachineKinds.TryParse(reader.GetString(4), out var parsed) ? parsed : MachineKind.Intermittent;
        }

        return new Pairing(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Enum.Parse<PairingState>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            kind,
            StorageText.Parse(reader.GetString(5)),
            StorageText.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }
}
