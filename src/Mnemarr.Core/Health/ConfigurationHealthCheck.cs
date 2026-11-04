using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Declarative;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the configuration as code: when a file is set, it was applied. One that was not applied is degraded with the reason (the
/// reason gives no path), because an infrastructure that does not match its description is not the one that was asked for.
/// </summary>
public sealed class ConfigurationHealthCheck : IHealthCheck
{
    private readonly IOptions<ConfigurationOptions> _options;
    private readonly ConfigurationState _state;

    public ConfigurationHealthCheck(IOptions<ConfigurationOptions> options, ConfigurationState state)
    {
        _options = options;
        _state = state;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.File))
        {
            return Task.FromResult(HealthCheckResult.Healthy());
        }

        if (_state.Error is { } error)
        {
            return Task.FromResult(HealthReasons.Degraded("config-invalid", $"The configuration file was not applied: {error}"));
        }

        return Task.FromResult(_state.Attempted
            ? HealthCheckResult.Healthy()
            : HealthReasons.Degraded("config-pending", "The configuration file has not been applied yet."));
    }
}
