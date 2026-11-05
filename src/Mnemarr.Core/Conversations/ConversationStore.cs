using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Conversations;

/// <summary>A conversation that a collector sent: how many of its lines were taken, which is the position the collector resumes from.</summary>
public sealed record ConversationRecord(string Machine, string Session, string Application, string Format, long Lines, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, string NotePath);

/// <summary>What is known of a machine that sent conversations: that it is not silent is what tells an agent that works from one that does not.</summary>
public sealed record MachineActivity(string Machine, int Conversations, long Lines, DateTimeOffset LastSeen);

public interface IConversationStore
{
    ConversationRecord? Find(string machine, string session);

    void Save(ConversationRecord record);

    IReadOnlyList<MachineActivity> Machines();
}

public sealed class SqliteConversationStore : IConversationStore
{
    private readonly IDatabase _database;

    public SqliteConversationStore(IDatabase database)
    {
        _database = database;
    }

    public ConversationRecord? Find(string machine, string session)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT machine, session, application, format, lines, started_at, updated_at, note_path FROM conversations WHERE machine = $machine AND session = $session";
        command.Parameters.AddWithValue("$machine", machine);
        command.Parameters.AddWithValue("$session", session);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ConversationRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), StorageText.Parse(reader.GetString(5)), StorageText.Parse(reader.GetString(6)), reader.GetString(7))
            : null;
    }

    public void Save(ConversationRecord record)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations (machine, session, application, format, lines, started_at, updated_at, note_path)
            VALUES ($machine, $session, $application, $format, $lines, $started, $updated, $path)
            ON CONFLICT (machine, session) DO UPDATE SET lines = $lines, updated_at = $updated, started_at = $started, note_path = $path
            """;
        command.Parameters.AddWithValue("$machine", record.Machine);
        command.Parameters.AddWithValue("$session", record.Session);
        command.Parameters.AddWithValue("$application", record.Application);
        command.Parameters.AddWithValue("$format", record.Format);
        command.Parameters.AddWithValue("$lines", record.Lines);
        command.Parameters.AddWithValue("$started", StorageText.Format(record.StartedAt));
        command.Parameters.AddWithValue("$updated", StorageText.Format(record.UpdatedAt));
        command.Parameters.AddWithValue("$path", record.NotePath);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MachineActivity> Machines()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT machine, COUNT(*), SUM(lines), MAX(updated_at) FROM conversations GROUP BY machine ORDER BY machine";
        using var reader = command.ExecuteReader();
        var machines = new List<MachineActivity>();
        while (reader.Read())
        {
            machines.Add(new MachineActivity(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), StorageText.Parse(reader.GetString(3))));
        }

        return machines;
    }
}
