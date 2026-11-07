using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Embeddings;

/// <summary>What the indexer says of itself, for the health page: whether it can work, why not, and how far it is.</summary>
public sealed record EmbeddingReport(EmbeddingState State, string? Reason, EmbeddingProgress? Progress, string Model);

/// <summary>
/// The automatic part of the search by meaning (D-16, ADR 0034): in the background, the chunks that have no vector of the active model are computed, a batch at a
/// time, so that the work is incremental, resumable after a restart and never blocks a request. It follows the index: a note that changed has new chunks, which
/// are computed at the next pass. A pass that fails is kept as a reason and tried again.
/// </summary>
public sealed partial class EmbeddingIndexer : BackgroundService
{
    private const int Tidyings = 20;

    private readonly IEmbeddingProvider _provider;
    private readonly IVectorStore _store;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<EmbeddingIndexer> _logger;
    private volatile string? _failure;

    public EmbeddingIndexer(IEmbeddingProvider provider, IVectorStore store, IOptions<EmbeddingOptions> options, ILogger<EmbeddingIndexer> logger)
    {
        _provider = provider;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Where the search by meaning is at: off, failed (with the reason) or ready (with the progress).</summary>
    public EmbeddingReport Report()
    {
        if (!_options.Wanted)
        {
            return new EmbeddingReport(EmbeddingState.Off, null, null, _provider.ModelId);
        }

        // The report never loads the model (a probe must not wait for it): until the first pass has, the feature is starting.
        if (_provider.Known() is not { } status)
        {
            return new EmbeddingReport(EmbeddingState.Starting, null, null, _provider.ModelId);
        }

        if (status.State == EmbeddingState.Failed)
        {
            return new EmbeddingReport(EmbeddingState.Failed, status.Reason, null, _provider.ModelId);
        }

        if (!_store.TryPrepare(out var reason))
        {
            return new EmbeddingReport(EmbeddingState.Failed, reason, null, _provider.ModelId);
        }

        return _failure is { } failure
            ? new EmbeddingReport(EmbeddingState.Failed, failure, Progress(), _provider.ModelId)
            : new EmbeddingReport(EmbeddingState.Ready, null, Progress(), _provider.ModelId);
    }

    /// <summary>One pass: computes every chunk that is missing, a batch at a time. Public so that a test can run it. Returns how many were computed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Wanted || _provider.Status().State != EmbeddingState.Ready || !_store.TryPrepare(out _))
        {
            return 0;
        }

        var computed = 0;
        for (var batch = await Task.Run(() => _store.Missing(_provider.ModelId, _options.BatchSize * 4), cancellationToken); batch.Count > 0; batch = _store.Missing(_provider.ModelId, _options.BatchSize * 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunks = batch;
            // The model is the CPU's work: it runs on a thread of the pool, not on the one that serves requests.
            await Task.Run(
                () =>
                {
                    var vectors = _provider.EmbedPassages([.. chunks.Select(chunk => chunk.Text)]);
                    _store.Save(_provider.ModelId, _provider.Dimension, [.. chunks.Select((chunk, index) => (chunk.ChunkId, vectors[index]))]);
                },
                cancellationToken);
            computed += chunks.Count;
        }

        if (computed > 0)
        {
            LogComputed(computed);
        }

        return computed;
    }

    /// <summary>Forgets the vectors of chunks that are gone (a note that was changed or removed) and the tables of models that nothing uses. Public so that a test can run it.</summary>
    public Task TidyAsync(CancellationToken cancellationToken) =>
        _options.Wanted && _provider.Status().State == EmbeddingState.Ready && _store.TryPrepare(out _)
            ? Task.Run(() => _store.Tidy(_provider.ModelId, _provider.Dimension), cancellationToken)
            : Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Loading the model and the first pass are long and synchronous for a while: not on the thread that starts the host.
        await Task.Yield();
        if (!_options.Wanted)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IdleSeconds));
        var passes = 0;
        do
        {
            try
            {
                // The chunks of a note that changed are gone and have no use for their vectors: they are forgotten after a pass that computed, and now and then.
                if (await RunAsync(stoppingToken) > 0 || ++passes % Tidyings == 0)
                {
                    await TidyAsync(stoppingToken);
                }

                _failure = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The loop of a background service is where everything stops: an optional feature must not take the host down, whatever went wrong.
                // The detail is in the log; what is said to the people who ask where the search is at is not a path of the host.
                _failure = "The last pass failed: see the log of Mnemarr.";
                LogFailed(ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private EmbeddingProgress? Progress()
    {
        try
        {
            return _store.Progress(_provider.ModelId);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} passages were computed for the search by meaning.")]
    private partial void LogComputed(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "A pass of the search by meaning failed; it is tried again.")]
    private partial void LogFailed(Exception exception);
}
