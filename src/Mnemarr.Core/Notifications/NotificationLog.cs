using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Notifications;

/// <summary>A mail that was tried: for which machine (null for a test), when, whether it went through and, if not, why.</summary>
public sealed record NotificationAttempt(string? Machine, DateTimeOffset At, bool Delivered, string? Reason);

/// <summary>What was tried, kept so that the daily caps hold across a restart and the page can say why a mail did not leave (ADR 0042).</summary>
public interface INotificationLog
{
    void Record(string? machine, DateTimeOffset at, bool delivered, string? reason);

    /// <summary>How many mails were tried since a moment, for one machine or, when none is named, all of them.</summary>
    int CountSince(DateTimeOffset since, string? machine = null);

    /// <summary>The last attempt, for one machine or, when none is named, of any kind. Null when there was none.</summary>
    NotificationAttempt? Last(string? machine = null);

    /// <summary>Forgets the attempts that are older than a moment.</summary>
    void Prune(DateTimeOffset before);
}

public sealed class SqliteNotificationLog : INotificationLog
{
    private readonly IDatabase _database;

    public SqliteNotificationLog(IDatabase database) => _database = database;

    public void Record(string? machine, DateTimeOffset at, bool delivered, string? reason)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO notification_log (machine, at, delivered, reason) VALUES ($machine, $at, $delivered, $reason)";
        command.Parameters.AddWithValue("$machine", (object?)machine ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", StorageText.Format(at));
        command.Parameters.AddWithValue("$delivered", delivered ? 1 : 0);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public int CountSince(DateTimeOffset since, string? machine = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM notification_log WHERE at >= $since AND ($machine IS NULL OR machine = $machine)";
        command.Parameters.AddWithValue("$since", StorageText.Format(since.ToUniversalTime()));
        command.Parameters.AddWithValue("$machine", (object?)machine ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public NotificationAttempt? Last(string? machine = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT machine, at, delivered, reason FROM notification_log WHERE ($machine IS NULL OR machine = $machine) ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$machine", (object?)machine ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var forMachine = reader.IsDBNull(0) ? null : reader.GetString(0);
        var reason = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new NotificationAttempt(forMachine, StorageText.Parse(reader.GetString(1)), reader.GetInt32(2) == 1, reason);
    }

    public void Prune(DateTimeOffset before)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notification_log WHERE at < $before";
        command.Parameters.AddWithValue("$before", StorageText.Format(before.ToUniversalTime()));
        command.ExecuteNonQuery();
    }
}
