using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the storage: the database opens and has the schema this version expects. One that cannot be opened, or was written by a
/// newer version, is unhealthy. The description never reveals a path.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDatabase _database;

    public DatabaseHealthCheck(IDatabase database)
    {
        _database = database;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = _database.Open();
            // A database that was opened by this instance has the schema it applied; one changed under a running instance, by a restore or a
            // newer version started on the same folder, does not.
            return Task.FromResult(Schema.CurrentVersion(connection) == Schema.Version
                ? HealthCheckResult.Healthy()
                : HealthReasons.Unhealthy("schema-mismatch", "The database does not have the schema of this version."));
        }
        catch (UnsupportedSchemaException ex)
        {
            return Task.FromResult(HealthReasons.Unhealthy("schema-newer", "The database was written by a newer version of Mnemarr.", ex));
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            return Task.FromResult(HealthReasons.Unhealthy("database-unavailable", "The database cannot be opened.", ex));
        }
    }
}
