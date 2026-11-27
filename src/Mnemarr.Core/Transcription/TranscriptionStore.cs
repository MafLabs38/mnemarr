using System.Globalization;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Transcription;

/// <summary>
/// What came of an attempt. <see cref="Pending"/> while the service works, then whether there is a text, a failure on the service's side, a recording the service refused, or a
/// call that was cancelled (the service may still count it: it stays in the cap).
/// </summary>
public enum TranscriptionOutcome
{
    Pending,
    Succeeded,
    ServiceFailed,
    RecordingRefused,
    Cancelled,
}

/// <summary>
/// One attempt at a transcription: who, when, how big, with which model, what came of it, and the fingerprint of the setup it was made with. Never the text, never the
/// sound.
/// </summary>
public sealed record TranscriptionAttempt(
    string Actor,
    DateTimeOffset At,
    long Bytes,
    string Model,
    TranscriptionOutcome Outcome,
    double? Seconds,
    string? Reason,
    string? Setup);

/// <summary>The model an administrator chose in the interface (null: the deployment's, then the catalog's first), and the attempts, which the daily cap counts.</summary>
public interface ITranscriptionStore
{
    string? ChosenModel();

    void ChooseModel(string? model);

    /// <summary>
    /// Writes a pending attempt if this person sent fewer than <paramref name="max"/> recordings since <paramref name="since"/>, and returns its id; null when the cap is reached.
    /// The count and the write are one statement: recordings sent at the same time cannot all pass it.
    /// </summary>
    long? Reserve(string actor, DateTimeOffset at, long bytes, string model, string setup, DateTimeOffset since, int max);

    /// <summary>Writes what came of a reserved attempt.</summary>
    void Complete(long id, TranscriptionOutcome outcome, double? seconds, string? reason);

    /// <summary>How many recordings this person sent since <paramref name="since"/>, whatever came of them.</summary>
    int CountSince(string actor, DateTimeOffset since);

    /// <summary>When this person's oldest recording since <paramref name="since"/> was sent, or null: the cap frees a place one day after it.</summary>
    DateTimeOffset? OldestSince(string actor, DateTimeOffset since);

    /// <summary>The latest attempt that says something about the service (a text, or a failure on its side), or null: what the readiness probe looks at.</summary>
    TranscriptionAttempt? LastOfService();

    /// <summary>Forgets the attempts older than <paramref name="before"/>: the cap looks back one day, the probe at the last one.</summary>
    void Prune(DateTimeOffset before);
}

/// <summary>The choice is one row (<c>transcription_settings</c>, id 1) and the attempts a small log (<c>transcription_log</c>), in the same database as the rest.</summary>
public sealed class SqliteTranscriptionStore : ITranscriptionStore
{
    private readonly IDatabase _database;

    public SqliteTranscriptionStore(IDatabase database) => _database = database;

    public string? ChosenModel()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model FROM transcription_settings WHERE id = 1";
        return command.ExecuteScalar() as string;
    }

    public void ChooseModel(string? model) =>
        Execute("INSERT INTO transcription_settings (id, model) VALUES (1, $model) ON CONFLICT (id) DO UPDATE SET model = excluded.model", command =>
            command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value));

    public long? Reserve(string actor, DateTimeOffset at, long bytes, string model, string setup, DateTimeOffset since, int max)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transcription_log (actor, at, bytes, model, outcome, setup)
            SELECT $actor, $at, $bytes, $model, $outcome, $setup
            WHERE (SELECT COUNT(*) FROM transcription_log WHERE actor = $actor AND at >= $since) < $max
            RETURNING id
            """;
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$at", Stamp(at));
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$setup", setup);
        command.Parameters.AddWithValue("$outcome", nameof(TranscriptionOutcome.Pending));
        command.Parameters.AddWithValue("$since", Stamp(since));
        command.Parameters.AddWithValue("$max", max);
        return command.ExecuteScalar() is long id ? id : null;
    }

    public void Complete(long id, TranscriptionOutcome outcome, double? seconds, string? reason) =>
        Execute("UPDATE transcription_log SET outcome = $outcome, seconds = $seconds, reason = $reason WHERE id = $id", command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$outcome", outcome.ToString());
            command.Parameters.AddWithValue("$seconds", (object?)seconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        });

    public int CountSince(string actor, DateTimeOffset since)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM transcription_log WHERE actor = $actor AND at >= $since";
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$since", Stamp(since));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public DateTimeOffset? OldestSince(string actor, DateTimeOffset since)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(at) FROM transcription_log WHERE actor = $actor AND at >= $since";
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$since", Stamp(since));
        return command.ExecuteScalar() is string at ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture) : null;
    }

    public TranscriptionAttempt? LastOfService()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT actor, at, bytes, model, outcome, seconds, reason, setup FROM transcription_log
            WHERE outcome IN ($succeeded, $failed) ORDER BY id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$succeeded", nameof(TranscriptionOutcome.Succeeded));
        command.Parameters.AddWithValue("$failed", nameof(TranscriptionOutcome.ServiceFailed));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        double? seconds = reader.IsDBNull(5) ? null : reader.GetDouble(5);
        var reason = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new TranscriptionAttempt(
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetInt64(2),
            reader.GetString(3),
            Enum.Parse<TranscriptionOutcome>(reader.GetString(4)),
            seconds,
            reason,
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public void Prune(DateTimeOffset before) =>
        Execute("DELETE FROM transcription_log WHERE at < $before", command => command.Parameters.AddWithValue("$before", Stamp(before)));

    /// <summary>A sortable text in UTC, so that a comparison of texts is a comparison of times.</summary>
    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private void Execute(string sql, Action<SqliteCommand> parameters)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        parameters(command);
        command.ExecuteNonQuery();
    }
}
