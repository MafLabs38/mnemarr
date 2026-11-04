using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the sources: the base is there. A kind the rules require and nobody has declared makes Mnemarr degraded, with the kind,
/// because what depends on it (the memory) cannot work. A source whose last pass failed (its folder unmounted, moved out of the roots,
/// unreadable) keeps its old notes and stays searchable, so it is reported too, by name. A database that cannot be read is reported by the
/// database check.
/// </summary>
public sealed class SourcesHealthCheck : IHealthCheck
{
    private readonly SourceCatalog _catalog;
    private readonly ScanOutcomes _outcomes;

    public SourcesHealthCheck(SourceCatalog catalog, ScanOutcomes outcomes)
    {
        _catalog = catalog;
        _outcomes = outcomes;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var missing = _catalog.Kinds().Where(status => !status.Satisfied).Select(status => status.Rule.Kind.ToString()).ToList();
            if (missing.Count > 0)
            {
                return Task.FromResult(HealthReasons.Degraded("source-missing", $"A required source is not declared: {string.Join(", ", missing)}."));
            }

            var unreadable = _catalog.List(SourceScope.All).Where(source => _outcomes.Of(source.Id)?.Failed == true).Select(source => source.Name).ToList();
            return Task.FromResult(unreadable.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthReasons.Degraded("source-unreadable", $"The last scan of a source failed: {string.Join(", ", unreadable)}."));
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            return Task.FromResult(HealthReasons.Unhealthy("sources-unreadable", "The sources cannot be read.", ex));
        }
    }
}
