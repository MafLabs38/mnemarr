using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the collectors (ADR 0027 §8): an always-on machine that has been silent for too long, or that reports an error, is degraded
/// with its reason. A machine that sleeps (intermittent) never is: its absence is ordinary.
/// </summary>
public sealed class CollectorsHealthCheck : IHealthCheck
{
    private readonly CollectorService _collectors;

    public CollectorsHealthCheck(CollectorService collectors)
    {
        _collectors = collectors;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(Check());
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            // The database check reports a database that cannot be read.
            return Task.FromResult(HealthCheckResult.Healthy());
        }
    }

    private HealthCheckResult Check()
    {
        var alerts = _collectors.Alerts();
        var failing = alerts.Where(alert => alert.State == MachineState.Failing).Select(alert => alert.Machine.Name).ToList();
        if (failing.Count > 0)
        {
            return HealthReasons.Degraded("collector-failing", $"A collector reports an error: {string.Join(", ", failing)}.");
        }

        var silent = alerts.Select(alert => alert.Machine.Name).ToList();
        return silent.Count > 0
            ? HealthReasons.Degraded("collector-silent", $"An always-on machine has been silent for too long: {string.Join(", ", silent)}.")
            : HealthCheckResult.Healthy();
    }
}
