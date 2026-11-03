using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness: the database opens and has the schema this version expects, and somebody can sign in. A database that cannot be opened is
/// unhealthy; one with no account at all is degraded, with the reason, because an empty Mnemarr nobody can enter is not a working one. The
/// description never reveals a path.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDatabase _database;
    private readonly IAccountStore _accounts;

    public DatabaseHealthCheck(IDatabase database, IAccountStore accounts)
    {
        _database = database;
        _accounts = accounts;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != Schema.Version)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("The database does not have the schema of this version."));
            }

            return Task.FromResult(_accounts.Count() == 0
                ? HealthCheckResult.Degraded("No account exists: the first administrator is not configured, nobody can sign in.")
                : HealthCheckResult.Healthy());
        }
        catch (Exception ex) when (ex is SqliteException or DirectoryNotFoundException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The database cannot be opened.", ex));
        }
    }
}
