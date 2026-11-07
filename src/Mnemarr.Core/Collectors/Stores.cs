using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Collectors;

public interface IMachineStore
{
    Machine? Get(string name);

    Machine? GetByAccount(Guid accountId);

    /// <summary>The machine that a computer is, whatever it is called now.</summary>
    Machine? GetByComputer(string computerId);

    IReadOnlyList<Machine> List();

    /// <summary>Gives a machine another name, and keeps the old one as a name it still answers to. False when there is no such machine.</summary>
    bool Rename(string name, string newName);

    /// <summary>The machine that a former name belongs to, or null.</summary>
    string? AliasOf(string alias);

    /// <summary>Adds the machine, or puts a machine that enrols again back as it was just enrolled (a new account, no past, not revoked).</summary>
    void Enrol(Machine machine);

    /// <summary>The machine was heard from; a heartbeat also says its version, what it has left to send and its error.</summary>
    void Seen(string name, DateTimeOffset at, Heartbeat? heartbeat);

    bool Revoke(string name, DateTimeOffset at);

    /// <summary>Gives a machine that has no identifier the one of its computer. False when it has one, or when another machine holds it.</summary>
    bool SetComputer(string name, string computerId);

    /// <summary>Takes a revoked machine out of the list. False when there is none, or it is not revoked.</summary>
    bool Remove(string name);
}

public interface IEnrolmentStore
{
    void Add(Enrolment enrolment, string codeHash);

    Enrolment? Get(Guid id);

    Enrolment? FindByCodeHash(string codeHash);

    /// <summary>The requests that are not over, newest first, and how many are waiting for a person.</summary>
    IReadOnlyList<Enrolment> Open(int limit);

    int CountPending(DateTimeOffset now);

    bool Approve(Guid id, string approvedBy, DateTimeOffset at, DateTimeOffset expiresAt);

    bool Reject(Guid id);

    /// <summary>Marks an approved code as used: true for the one caller that got there first.</summary>
    bool Use(Guid id);
}

public sealed class SqliteMachineStore : IMachineStore
{
    private const string NameParameter = "$name";
    private const string MachineColumns = "name, kind, account_id, enrolled_at, last_seen_at, version, pending_lines, error, revoked_at, paused, computer_id, platform, program";
    private readonly IDatabase _database;

    public SqliteMachineStore(IDatabase database)
    {
        _database = database;
    }

    public Machine? Get(string name) => One($"SELECT {MachineColumns} FROM machines WHERE name = $key", name);

    public Machine? GetByAccount(Guid accountId) => One($"SELECT {MachineColumns} FROM machines WHERE account_id = $key", accountId.ToString());

    public Machine? GetByComputer(string computerId) => One($"SELECT {MachineColumns} FROM machines WHERE computer_id = $key", computerId);

    public string? AliasOf(string alias)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT machine FROM machine_aliases WHERE alias = $alias";
        command.Parameters.AddWithValue("$alias", alias);
        return command.ExecuteScalar() as string;
    }

    public bool Rename(string name, string newName)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE machines SET name = $new WHERE name = $name;
            UPDATE machine_aliases SET machine = $new WHERE machine = $name;
            DELETE FROM machine_aliases WHERE alias = $new;
            INSERT INTO machine_aliases (alias, machine) SELECT $name, $new WHERE EXISTS (SELECT 1 FROM machines WHERE name = $new);
            """;
        command.Parameters.AddWithValue(NameParameter, name);
        command.Parameters.AddWithValue("$new", newName);
        var renamed = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        return renamed;
    }

    public IReadOnlyList<Machine> List()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {MachineColumns} FROM machines ORDER BY name";
        using var reader = command.ExecuteReader();
        var machines = new List<Machine>();
        while (reader.Read())
        {
            machines.Add(Read(reader));
        }

        return machines;
    }

    public void Enrol(Machine machine)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO machines (name, kind, account_id, enrolled_at, last_seen_at, version, pending_lines, error, revoked_at, paused, computer_id)
            VALUES ($name, $kind, $account, $enrolled, NULL, NULL, 0, NULL, NULL, 0, $computer)
            ON CONFLICT (name) DO UPDATE SET kind = $kind, account_id = $account, enrolled_at = $enrolled, last_seen_at = NULL, version = NULL, pending_lines = 0, error = NULL, revoked_at = NULL, paused = 0, computer_id = coalesce($computer, computer_id)
            """;
        command.Parameters.AddWithValue("$computer", (object?)machine.ComputerId ?? DBNull.Value);
        command.Parameters.AddWithValue(NameParameter, machine.Name);
        command.Parameters.AddWithValue("$kind", MachineKinds.Name(machine.Kind));
        command.Parameters.AddWithValue("$account", machine.AccountId.ToString());
        command.Parameters.AddWithValue("$enrolled", StorageText.Format(machine.EnrolledAt));
        command.ExecuteNonQuery();
    }

    public void Seen(string name, DateTimeOffset at, Heartbeat? heartbeat)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = heartbeat is null
            ? "UPDATE machines SET last_seen_at = $at WHERE name = $name"
            : "UPDATE machines SET last_seen_at = $at, version = $version, pending_lines = $pending, error = $error, paused = $paused, platform = $platform, program = $program WHERE name = $name";
        command.Parameters.AddWithValue(NameParameter, name);
        command.Parameters.AddWithValue("$at", StorageText.Format(at));
        if (heartbeat is not null)
        {
            command.Parameters.AddWithValue("$version", heartbeat.Version);
            command.Parameters.AddWithValue("$pending", heartbeat.PendingLines);
            command.Parameters.AddWithValue("$error", (object?)heartbeat.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$paused", heartbeat.Paused ? 1 : 0);
            command.Parameters.AddWithValue("$platform", (object?)heartbeat.Platform ?? DBNull.Value);
            command.Parameters.AddWithValue("$program", (object?)heartbeat.Program ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    public bool Revoke(string name, DateTimeOffset at)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE machines SET revoked_at = $at WHERE name = $name AND revoked_at IS NULL";
        command.Parameters.AddWithValue(NameParameter, name);
        command.Parameters.AddWithValue("$at", StorageText.Format(at));
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetComputer(string name, string computerId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE machines SET computer_id = $computer WHERE name = $name AND computer_id IS NULL AND NOT EXISTS (SELECT 1 FROM machines WHERE computer_id = $computer)";
        command.Parameters.AddWithValue(NameParameter, name);
        command.Parameters.AddWithValue("$computer", computerId);
        return command.ExecuteNonQuery() > 0;
    }

    public bool Remove(string name)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM machine_aliases WHERE machine = $name AND EXISTS (SELECT 1 FROM machines WHERE name = $name AND revoked_at IS NOT NULL); DELETE FROM machines WHERE name = $name AND revoked_at IS NOT NULL";
        command.Parameters.AddWithValue(NameParameter, name);
        return command.ExecuteNonQuery() > 0;
    }

    private Machine? One(string query, string key)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static Machine Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        MachineKinds.TryParse(reader.GetString(1), out var kind) ? kind : MachineKind.Intermittent,
        Guid.Parse(reader.GetString(2)),
        StorageText.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : StorageText.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : StorageText.Parse(reader.GetString(8)),
        reader.GetInt32(9) != 0,
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12));
}

public sealed class SqliteEnrolmentStore : IEnrolmentStore
{
    private readonly IDatabase _database;

    public SqliteEnrolmentStore(IDatabase database)
    {
        _database = database;
    }

    public void Add(Enrolment enrolment, string codeHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO enrolments (id, machine, kind, state, code_hash, requested_at, requested_by, approved_at, approved_by, expires_at)
            VALUES ($id, $machine, $kind, $state, $hash, $requested, $by, $approvedAt, $approvedBy, $expires)
            """;
        command.Parameters.AddWithValue("$id", enrolment.Id.ToString());
        command.Parameters.AddWithValue("$machine", enrolment.Machine);
        command.Parameters.AddWithValue("$kind", MachineKinds.Name(enrolment.Kind));
        command.Parameters.AddWithValue("$state", enrolment.State.ToString());
        command.Parameters.AddWithValue("$hash", codeHash);
        command.Parameters.AddWithValue("$requested", StorageText.Format(enrolment.RequestedAt));
        command.Parameters.AddWithValue("$by", enrolment.RequestedBy);
        command.Parameters.AddWithValue("$approvedAt", enrolment.ApprovedAt is { } at ? StorageText.Format(at) : DBNull.Value);
        command.Parameters.AddWithValue("$approvedBy", (object?)enrolment.ApprovedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$expires", StorageText.Format(enrolment.ExpiresAt));
        command.ExecuteNonQuery();
    }

    public Enrolment? Get(Guid id) => One("SELECT id, machine, kind, state, requested_at, requested_by, approved_at, approved_by, expires_at FROM enrolments WHERE id = $key", id.ToString());

    public Enrolment? FindByCodeHash(string codeHash) => One("SELECT id, machine, kind, state, requested_at, requested_by, approved_at, approved_by, expires_at FROM enrolments WHERE code_hash = $key", codeHash);

    public IReadOnlyList<Enrolment> Open(int limit)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, machine, kind, state, requested_at, requested_by, approved_at, approved_by, expires_at FROM enrolments WHERE state IN ('Pending', 'Approved') ORDER BY requested_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var found = new List<Enrolment>();
        while (reader.Read())
        {
            found.Add(Read(reader));
        }

        return found;
    }

    public int CountPending(DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM enrolments WHERE state = 'Pending' AND expires_at > $now";
        command.Parameters.AddWithValue("$now", StorageText.Format(now));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool Approve(Guid id, string approvedBy, DateTimeOffset at, DateTimeOffset expiresAt)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE enrolments SET state = 'Approved', approved_by = $by, approved_at = $at, expires_at = $expires WHERE id = $id AND state = 'Pending'";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$by", approvedBy);
        command.Parameters.AddWithValue("$at", StorageText.Format(at));
        command.Parameters.AddWithValue("$expires", StorageText.Format(expiresAt));
        return command.ExecuteNonQuery() > 0;
    }

    public bool Reject(Guid id) => Change("UPDATE enrolments SET state = 'Rejected' WHERE id = $id AND state IN ('Pending', 'Approved')", id);

    public bool Use(Guid id) => Change("UPDATE enrolments SET state = 'Used' WHERE id = $id AND state = 'Approved'", id);

    private bool Change(string update, Guid id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = update;
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteNonQuery() > 0;
    }

    private Enrolment? One(string query, string key)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static Enrolment Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        MachineKinds.TryParse(reader.GetString(2), out var kind) ? kind : MachineKind.Intermittent,
        Enum.Parse<EnrolmentState>(reader.GetString(3)),
        StorageText.Parse(reader.GetString(4)),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : StorageText.Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        StorageText.Parse(reader.GetString(8)));
}
