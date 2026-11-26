using System.Globalization;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Transcription;

/// <summary>One attempt at a transcription: who, when, how big, with which model, what came of it. Never the text, never the sound.</summary>
public sealed record TranscriptionAttempt(string Actor, DateTimeOffset At, long Bytes, string Model, bool Succeeded, double? Seconds, string? Reason);

/// <summary>The model an administrator chose in the interface (null: the deployment's, then the catalog's first), and the attempts, which the daily cap counts.</summary>
public interface ITranscriptionStore
{
    string? ChosenModel();

    void ChooseModel(string? model);

    void Record(TranscriptionAttempt attempt);

    /// <summary>How many recordings this person sent since <paramref name="since"/>, whatever came of them.</summary>
    int CountSince(string actor, DateTimeOffset since);

    /// <summary>The latest attempt of anyone, or null: what the readiness probe looks at.</summary>
    TranscriptionAttempt? Last();

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

    public void Record(TranscriptionAttempt attempt) =>
        Execute(
            "INSERT INTO transcription_log (actor, at, bytes, model, succeeded, seconds, reason) VALUES ($actor, $at, $bytes, $model, $succeeded, $seconds, $reason)",
            command =>
            {
                command.Parameters.AddWithValue("$actor", attempt.Actor);
                command.Parameters.AddWithValue("$at", Stamp(attempt.At));
                command.Parameters.AddWithValue("$bytes", attempt.Bytes);
                command.Parameters.AddWithValue("$model", attempt.Model);
                command.Parameters.AddWithValue("$succeeded", attempt.Succeeded ? 1 : 0);
                command.Parameters.AddWithValue("$seconds", (object?)attempt.Seconds ?? DBNull.Value);
                command.Parameters.AddWithValue("$reason", (object?)attempt.Reason ?? DBNull.Value);
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

    public TranscriptionAttempt? Last()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT actor, at, bytes, model, succeeded, seconds, reason FROM transcription_log ORDER BY id DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        double? seconds = reader.IsDBNull(5) ? null : reader.GetDouble(5);
        var reason = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new TranscriptionAttempt(
            reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4) == 1, seconds, reason);
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
