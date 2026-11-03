using System.Globalization;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Accounts;

public sealed class SqliteAuditLog : IAuditLog
{
    private const int MaxText = 500;
    private readonly IDatabase _database;
    private readonly TimeProvider _clock;

    public SqliteAuditLog(IDatabase database, TimeProvider clock)
    {
        _database = database;
        _clock = clock;
    }

    public void Append(string actor, string action, string target, string detail = "")
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO audit_events (at, actor, action, target, detail) VALUES ($at, $actor, $action, $target, $detail)";
        command.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actor", Cut(actor));
        command.Parameters.AddWithValue("$action", Cut(action));
        command.Parameters.AddWithValue("$target", Cut(target));
        command.Parameters.AddWithValue("$detail", Cut(detail));
        command.ExecuteNonQuery();
    }

    public int Prune(DateTimeOffset before)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM audit_events WHERE at < $before";
        command.Parameters.AddWithValue("$before", before.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEvent> Recent(int limit, string? action = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, at, actor, action, target, detail FROM audit_events WHERE ($action IS NULL OR action = $action) ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$action", action ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var events = new List<AuditEvent>();
        while (reader.Read())
        {
            events.Add(new AuditEvent(
                reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return events;
    }

    private static string Cut(string text) => text.Length <= MaxText ? text : text[..MaxText];
}
