using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the sources: the base is there. A kind the rules require and nobody has declared makes Mnemarr degraded, with the kind,
/// because what depends on it (the memory) cannot work. A database that cannot be read is reported by the database check.
/// </summary>
public sealed class SourcesHealthCheck : IHealthCheck
{
    private readonly SourceCatalog _catalog;

    public SourcesHealthCheck(SourceCatalog catalog)
    {
        _catalog = catalog;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var missing = _catalog.Kinds().Where(status => !status.Satisfied).Select(status => status.Rule.Kind.ToString()).ToList();
            return Task.FromResult(missing.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthReasons.Degraded("source-missing", $"A required source is not declared: {string.Join(", ", missing)}."));
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            return Task.FromResult(HealthReasons.Unhealthy("sources-unreadable", "The sources cannot be read.", ex));
        }
    }
}
