using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Health;

namespace Mnemarr.Api.V1.Consolidation;

/// <summary>
/// The consolidation (ADR 0036): off is not a fault; on, it needs the runner and a memory vault open for writing, and conversations that failed too often are said. Degraded, with a reason,
/// because everything else works without it.
/// </summary>
public sealed class ConsolidationHealthCheck : IHealthCheck
{
    private readonly Consolidator _consolidator;

    public ConsolidationHealthCheck(Consolidator consolidator)
    {
        _consolidator = consolidator;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = _consolidator.Status();
        return Task.FromResult(status.State switch
        {
            "off" => HealthCheckResult.Healthy("The consolidation is off."),
            "no-runner" => HealthReasons.Degraded("consolidation-no-runner", "The consolidation is on but there is no assistant runner (MNEMARR__AGENT__URL)."),
            "no-memory-vault" => HealthReasons.Degraded("consolidation-no-memory-vault", "The consolidation is on but there is no memory vault to write in."),
            "memory-vault-read-only" => HealthReasons.Degraded("consolidation-memory-vault-read-only", "The consolidation is on but the memory vault is read-only: turn writing on for it."),
            _ when status.Problem is not null => HealthReasons.Degraded("consolidation-runner-unreachable", status.Problem),
            _ when status.Failing > 0 => HealthReasons.Degraded("consolidation-failing", $"{status.Failing} conversation(s) could not be consolidated; the last reason: {status.LastFailure}"),
            _ => HealthCheckResult.Healthy($"The consolidation is on: {status.Waiting} conversation(s) wait, {status.Done} done."),
        });
    }
}
