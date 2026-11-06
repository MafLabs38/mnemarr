using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the sources: the base is there. A kind the rules require and nobody has declared makes Mnemarr degraded, with the kind,
/// because what depends on it (the memory) cannot work. A source whose last pass failed (its folder unmounted, moved out of the roots,
/// unreadable) keeps its old notes and stays searchable, so it is reported too, by name, and so is a Git source that cannot be brought up to
/// date (the host is down, the token was revoked): its checkout is old, which an operator must know. A database that cannot be read is reported by the
/// database check.
/// </summary>
public sealed class SourcesHealthCheck : IHealthCheck
{
    private readonly SourceCatalog _catalog;
    private readonly ScanOutcomes _outcomes;
    private readonly SyncOutcomes _syncs;

    public SourcesHealthCheck(SourceCatalog catalog, ScanOutcomes outcomes, SyncOutcomes syncs)
    {
        _catalog = catalog;
        _outcomes = outcomes;
        _syncs = syncs;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var missing = _catalog.Kinds().Where(status => !status.Satisfied).Select(status => status.Rule.Kind.ToString()).ToList();
            var sources = _catalog.List(SourceScope.All);
            var unreadable = sources.Where(source => _outcomes.Of(source.Id)?.Failed == true || _syncs.Of(source.Id)?.Failed == true).Select(source => source.Name).ToList();
            var failed = $"The last scan or sync of a source failed: {string.Join(", ", unreadable)}.";
            if (missing.Count > 0)
            {
                // Both are said: a source whose folder was unmounted must not hide behind the memory vault that is not there yet.
                var missingText = $"A required source is not declared: {string.Join(", ", missing)}.";
                return Task.FromResult(HealthReasons.Degraded("source-missing", unreadable.Count == 0 ? missingText : $"{missingText} {failed}"));
            }

            return Task.FromResult(unreadable.Count == 0 ? HealthCheckResult.Healthy() : HealthReasons.Degraded("source-unreadable", failed));
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            return Task.FromResult(HealthReasons.Unhealthy("sources-unreadable", "The sources cannot be read.", ex));
        }
    }
}
