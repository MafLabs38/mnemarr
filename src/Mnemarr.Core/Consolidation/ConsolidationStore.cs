using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Consolidation;

/// <summary>How far a conversation was turned into notes: the lines it held and the turns read at the time, how many notes the runs wrote, and how it went.</summary>
public sealed record ConsolidationRecord(string Machine, string Session, long LinesDone, int TurnsDone, int Notes, int Tries, string? Error, DateTimeOffset LastAt);

/// <summary>Where the consolidation stands, for the page and the health: what waits, what is done, what failed.</summary>
public sealed record ConsolidationCounts(int Waiting, int Done, int Failing, int Notes, int Today);

public interface IConsolidationStore
{
    ConsolidationRecord? Find(string machine, string session);

    void Save(ConsolidationRecord record);

    /// <summary>The conversations that grew since their last consolidation and have been left alone since <paramref name="idleBefore"/>, and did not fail too often: the oldest first.</summary>
    IReadOnlyList<(string Machine, string Session)> Pending(DateTimeOffset idleBefore, int maxTries, int limit);

    ConsolidationCounts Counts(DateTimeOffset dayStart, int maxTries);

    /// <summary>A run of the model, whatever came of it: what the day's cap counts (a conversation read in several runs, or tried again, costs each time).</summary>
    void CountRun(DateTimeOffset at);

    /// <summary>The last failure, for the page: what was said of it, and when.</summary>
    ConsolidationRecord? LastFailure();
}

public sealed class SqliteConsolidationStore : IConsolidationStore
{
    private const string Columns = "machine, session, lines_done, turns_done, notes, tries, error, last_at";
    private readonly IDatabase _database;

    public SqliteConsolidationStore(IDatabase database)
    {
        _database = database;
    }

    public ConsolidationRecord? Find(string machine, string session)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM consolidations WHERE machine = $machine AND session = $session";
        command.Parameters.AddWithValue("$machine", machine);
        command.Parameters.AddWithValue("$session", session);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Save(ConsolidationRecord record)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO consolidations (machine, session, lines_done, turns_done, notes, tries, error, last_at)
            VALUES ($machine, $session, $lines, $turns, $notes, $tries, $error, $at)
            ON CONFLICT (machine, session) DO UPDATE SET lines_done = $lines, turns_done = $turns, notes = $notes, tries = $tries, error = $error, last_at = $at
            """;
        command.Parameters.AddWithValue("$machine", record.Machine);
        command.Parameters.AddWithValue("$session", record.Session);
        command.Parameters.AddWithValue("$lines", record.LinesDone);
        command.Parameters.AddWithValue("$turns", record.TurnsDone);
        command.Parameters.AddWithValue("$notes", record.Notes);
        command.Parameters.AddWithValue("$tries", record.Tries);
        command.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", StorageText.Format(record.LastAt));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Machine, string Session)> Pending(DateTimeOffset idleBefore, int maxTries, int limit)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // A conversation that grew since the last run starts again with its tries: what failed was another text.
        command.CommandText = """
            SELECT c.machine, c.session FROM conversations c
            LEFT JOIN consolidations k ON k.machine = c.machine AND k.session = c.session
            WHERE c.updated_at <= $idle AND c.lines > coalesce(k.lines_done, 0)
              AND (coalesce(k.tries, 0) < $tries OR c.lines > coalesce(k.lines_done, 0) + 200)
            ORDER BY c.updated_at LIMIT $limit
            """;
        command.Parameters.AddWithValue("$idle", StorageText.Format(idleBefore));
        command.Parameters.AddWithValue("$tries", maxTries);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var found = new List<(string, string)>();
        while (reader.Read())
        {
            found.Add((reader.GetString(0), reader.GetString(1)));
        }

        return found;
    }

    public ConsolidationCounts Counts(DateTimeOffset dayStart, int maxTries)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM conversations c LEFT JOIN consolidations k ON k.machine = c.machine AND k.session = c.session WHERE c.lines > coalesce(k.lines_done, 0)),
              (SELECT COUNT(*) FROM consolidations WHERE error IS NULL),
              (SELECT COUNT(*) FROM consolidations WHERE error IS NOT NULL AND tries >= $tries),
              (SELECT coalesce(SUM(notes), 0) FROM consolidations),
              (SELECT coalesce(SUM(runs), 0) FROM consolidation_days WHERE day = $day)
            """;
        command.Parameters.AddWithValue("$tries", maxTries);
        command.Parameters.AddWithValue("$day", DayKey(dayStart));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new ConsolidationCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    public void CountRun(DateTimeOffset at)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO consolidation_days (day, runs) VALUES ($day, 1) ON CONFLICT (day) DO UPDATE SET runs = runs + 1";
        command.Parameters.AddWithValue("$day", DayKey(at));
        command.ExecuteNonQuery();
    }

    private static string DayKey(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public ConsolidationRecord? LastFailure()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM consolidations WHERE error IS NOT NULL ORDER BY last_at DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static ConsolidationRecord Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        StorageText.Parse(reader.GetString(7)));
}
