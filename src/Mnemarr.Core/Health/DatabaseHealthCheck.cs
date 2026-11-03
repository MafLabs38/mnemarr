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
            return Task.FromResult(Schema.CurrentVersion(connection) == Schema.Version
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database does not have the schema of this version."));
        }
        catch (UnsupportedSchemaException ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The database was written by a newer version of Mnemarr.", ex));
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The database cannot be opened.", ex));
        }
    }
}
