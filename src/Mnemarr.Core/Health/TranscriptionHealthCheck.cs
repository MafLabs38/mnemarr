using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Storage;
using Mnemarr.Core.Transcription;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the dictation (ADR 0047): when it is on and cannot work (the key cannot be read, or the last call failed on the service's side: a key, a quota, an address, a
/// timeout), it is degraded with the reason, never unhealthy. A recording the service refused says nothing about the setup and does not count. Off, it is always healthy.
/// </summary>
public sealed class TranscriptionHealthCheck : IHealthCheck
{
    private readonly IOptions<TranscriptionOptions> _options;
    private readonly IEnumerable<ITranscriptionProvider> _providers;
    private readonly ITranscriptionStore _store;

    public TranscriptionHealthCheck(IOptions<TranscriptionOptions> options, IEnumerable<ITranscriptionProvider> providers, ITranscriptionStore store)
    {
        _options = options;
        _providers = providers;
        _store = store;
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
        var options = _options.Value;
        if (!options.Enabled)
        {
            return HealthCheckResult.Healthy();
        }

        if (TranscriptionProviders.Find(_providers, options.Provider)?.CheckSecrets(options) is { } secret)
        {
            return HealthReasons.Degraded("transcription-secret", secret);
        }

        return _store.LastOfService() is { Outcome: TranscriptionOutcome.ServiceFailed } failed
            ? HealthReasons.Degraded("transcription-failing", $"The last recording got no text: {failed.Reason}")
            : HealthCheckResult.Healthy();
    }
}
