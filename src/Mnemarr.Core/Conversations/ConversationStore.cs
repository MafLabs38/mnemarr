using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Conversations;

/// <summary>Where the title of a conversation comes from (ADR 0028): what a person typed always wins over what a model wrote, which wins over the first sentence.</summary>
public static class TitleSources
{
    public const string FirstSentence = "first-sentence";
    public const string Summary = "summary";
    public const string Person = "person";

    public static bool IsKnown(string source) => source is FirstSentence or Summary or Person;
}

/// <summary>
/// A conversation that a collector sent: how many of its lines were taken, which is the position the collector resumes from; and its title, where
/// the title comes from, a short summary when a model wrote one, and how many times a model was tried for it.
/// </summary>
public sealed record ConversationRecord(
    string Machine,
    string Session,
    string Application,
    string Format,
    long Lines,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string NotePath,
    long NoteBytes = 0,
    string Account = "",
    string? Title = null,
    string TitleSource = TitleSources.FirstSentence,
    string? Summary = null,
    int TitleTries = 0,
    string? ComputerId = null);

/// <summary>How many conversations have a title of each origin.</summary>
public sealed record TitleCounts(int FirstSentence, int Summary, int Person);

/// <summary>What is known of a machine that sent conversations: that it is not silent is what tells an agent that works from one that does not.</summary>
public sealed record MachineActivity(string Machine, int Conversations, long Lines, DateTimeOffset LastSeen);

public interface IConversationStore
{
    ConversationRecord? Find(string machine, string session);

    void Save(ConversationRecord record);

    IReadOnlyList<MachineActivity> Machines();

    /// <summary>Every conversation held for a machine.</summary>
    IReadOnlyList<ConversationRecord> OfMachine(string machine);

    /// <summary>Whether any conversation is held for a machine (one row is read, not all of them).</summary>
    bool AnyOfMachine(string machine);

    /// <summary>The conversations of this session, on whichever machine they are held.</summary>
    IReadOnlyList<ConversationRecord> OfSession(string session);

    /// <summary>Files a conversation under another machine, with the note it now has. The row is the same: its lines, its title and its tries stay.</summary>
    void Move(string machine, string session, string toMachine, string notePath, long noteBytes);

    /// <summary>Forgets a conversation (its raw lines and its note are the caller's to remove).</summary>
    void Delete(string machine, string session);

    /// <summary>The conversation that is written in the note at this path.</summary>
    ConversationRecord? FindByNote(string notePath);

    /// <summary>Gives a conversation its title, and says where it comes from, with the length its note was left at.</summary>
    void SetTitle(string machine, string session, string title, string source, string? summary, long noteBytes);

    /// <summary>A model was tried for this conversation and did not give a title.</summary>
    void CountTry(string machine, string session);

    /// <summary>The conversations a model may write a title for: only a first sentence so far, not written to for a while, not tried too many times.</summary>
    IReadOnlyList<ConversationRecord> NeedingTitle(DateTimeOffset idleBefore, int maxTries, int limit);

    /// <summary>Puts conversations back to "only a first sentence", so that a model writes their title again. What a person typed is kept unless said otherwise. Returns how many.</summary>
    int RequestAgain(bool includePerson);

    TitleCounts TitleCounts();
}

public sealed class SqliteConversationStore : IConversationStore
{
    private const string Columns = "machine, session, application, format, lines, started_at, updated_at, note_path, note_bytes, account, title, title_source, summary, title_tries, computer_id";
    private const string MachineParameter = "$machine";
    private const string SessionParameter = "$session";
    private readonly IDatabase _database;

    public SqliteConversationStore(IDatabase database)
    {
        _database = database;
    }

    public ConversationRecord? Find(string machine, string session)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM conversations WHERE machine = $machine AND session = $session";
        command.Parameters.AddWithValue(MachineParameter, machine);
        command.Parameters.AddWithValue(SessionParameter, session);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static ConversationRecord Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
        StorageText.Parse(reader.GetString(5)), StorageText.Parse(reader.GetString(6)), reader.GetString(7), reader.GetInt64(8), reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt32(13), reader.IsDBNull(14) ? null : reader.GetString(14));

    public ConversationRecord? FindByNote(string notePath)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM conversations WHERE note_path = $path";
        command.Parameters.AddWithValue("$path", notePath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void SetTitle(string machine, string session, string title, string source, string? summary, long noteBytes)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET title = $title, title_source = $source, summary = $summary, note_bytes = $bytes, title_tries = 0 WHERE machine = $machine AND session = $session";
        command.Parameters.AddWithValue(MachineParameter, machine);
        command.Parameters.AddWithValue(SessionParameter, session);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", noteBytes);
        command.ExecuteNonQuery();
    }

    public void CountTry(string machine, string session)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET title_tries = title_tries + 1 WHERE machine = $machine AND session = $session";
        command.Parameters.AddWithValue(MachineParameter, machine);
        command.Parameters.AddWithValue(SessionParameter, session);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ConversationRecord> NeedingTitle(DateTimeOffset idleBefore, int maxTries, int limit)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM conversations WHERE title_source = 'first-sentence' AND title_tries < $tries AND updated_at <= $idle ORDER BY updated_at LIMIT $limit";
        command.Parameters.AddWithValue("$tries", maxTries);
        command.Parameters.AddWithValue("$idle", StorageText.Format(idleBefore));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var found = new List<ConversationRecord>();
        while (reader.Read())
        {
            found.Add(Read(reader));
        }

        return found;
    }

    public int RequestAgain(bool includePerson)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = includePerson
            ? "UPDATE conversations SET title_source = 'first-sentence', title_tries = 0 WHERE title_source <> 'first-sentence' OR title_tries <> 0"
            : "UPDATE conversations SET title_source = 'first-sentence', title_tries = 0 WHERE title_source = 'summary' OR (title_source = 'first-sentence' AND title_tries <> 0)";
        return command.ExecuteNonQuery();
    }

    public TitleCounts TitleCounts()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title_source, COUNT(*) FROM conversations GROUP BY title_source";
        using var reader = command.ExecuteReader();
        int first = 0, summary = 0, person = 0;
        while (reader.Read())
        {
            var count = reader.GetInt32(1);
            switch (reader.GetString(0))
            {
                case TitleSources.Summary:
                    summary = count;
                    break;
                case TitleSources.Person:
                    person = count;
                    break;
                default:
                    first += count;
                    break;
            }
        }

        return new TitleCounts(first, summary, person);
    }

    public void Save(ConversationRecord record)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations (machine, session, application, format, lines, started_at, updated_at, note_path, note_bytes, account, title, title_source, summary, computer_id)
            VALUES ($machine, $session, $application, $format, $lines, $started, $updated, $path, $bytes, $account, $title, $titleSource, $summary, $computer)
            ON CONFLICT (machine, session) DO UPDATE SET lines = $lines, updated_at = $updated, started_at = $started, note_path = $path, note_bytes = $bytes, account = $account, computer_id = coalesce($computer, computer_id)
            """;
        command.Parameters.AddWithValue(MachineParameter, record.Machine);
        command.Parameters.AddWithValue(SessionParameter, record.Session);
        command.Parameters.AddWithValue("$application", record.Application);
        command.Parameters.AddWithValue("$format", record.Format);
        command.Parameters.AddWithValue("$lines", record.Lines);
        command.Parameters.AddWithValue("$started", StorageText.Format(record.StartedAt));
        command.Parameters.AddWithValue("$updated", StorageText.Format(record.UpdatedAt));
        command.Parameters.AddWithValue("$path", record.NotePath);
        command.Parameters.AddWithValue("$bytes", record.NoteBytes);
        command.Parameters.AddWithValue("$account", record.Account);
        command.Parameters.AddWithValue("$title", (object?)record.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$titleSource", record.TitleSource);
        command.Parameters.AddWithValue("$summary", (object?)record.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$computer", (object?)record.ComputerId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ConversationRecord> OfMachine(string machine) => Many($"SELECT {Columns} FROM conversations WHERE machine = $key ORDER BY started_at", machine);

    public bool AnyOfMachine(string machine)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM conversations WHERE machine = $machine LIMIT 1";
        command.Parameters.AddWithValue(MachineParameter, machine);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<ConversationRecord> OfSession(string session) => Many($"SELECT {Columns} FROM conversations WHERE session = $key", session);

    private List<ConversationRecord> Many(string query, string key)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        var found = new List<ConversationRecord>();
        while (reader.Read())
        {
            found.Add(Read(reader));
        }

        return found;
    }

    public void Move(string machine, string session, string toMachine, string notePath, long noteBytes)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET machine = $to, note_path = $path, note_bytes = $bytes WHERE machine = $machine AND session = $session";
        command.Parameters.AddWithValue(MachineParameter, machine);
        command.Parameters.AddWithValue(SessionParameter, session);
        command.Parameters.AddWithValue("$to", toMachine);
        command.Parameters.AddWithValue("$path", notePath);
        command.Parameters.AddWithValue("$bytes", noteBytes);
        command.ExecuteNonQuery();
    }

    public void Delete(string machine, string session)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations WHERE machine = $machine AND session = $session";
        command.Parameters.AddWithValue(MachineParameter, machine);
        command.Parameters.AddWithValue(SessionParameter, session);
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
