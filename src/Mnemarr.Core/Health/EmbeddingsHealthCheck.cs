using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Embeddings;

namespace Mnemarr.Core.Health;

/// <summary>
/// The search by meaning (ADR 0034): off is not a fault (nobody gave a model); a model that cannot be loaded, a library that is missing or a pass that fails makes
/// Mnemarr degraded, with the reason, because the search is then by keywords only; and, while the first run over a long history goes on, the progress is said,
/// since the previous attempt at this product failed silently.
/// </summary>
public sealed class EmbeddingsHealthCheck : IHealthCheck
{
    private readonly EmbeddingIndexer _indexer;

    public EmbeddingsHealthCheck(EmbeddingIndexer indexer)
    {
        _indexer = indexer;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var report = _indexer.Report();
        return Task.FromResult(report.State switch
        {
            EmbeddingState.Failed => HealthReasons.Degraded("embeddings-failing", $"The search by meaning does not work, the search is by keywords only: {report.Reason}"),
            EmbeddingState.Starting => HealthCheckResult.Healthy("The search by meaning is starting."),
            EmbeddingState.Ready when report.Progress is { } progress && progress.Embedded < progress.Chunks =>
                HealthCheckResult.Healthy($"The search by meaning is catching up: {progress.Embedded} of {progress.Chunks} passages."),
            EmbeddingState.Ready => HealthCheckResult.Healthy("The search by meaning is up to date."),
            _ => HealthCheckResult.Healthy("The search by meaning is off."),
        });
    }
}
