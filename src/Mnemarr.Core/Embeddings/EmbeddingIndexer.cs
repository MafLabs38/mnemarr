using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;

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

    // How many batches of passages are looked up at once, and how long the progress is kept for the probes (a count over every chunk is not for each probe).
    private const int Lookahead = 4;
    private static readonly long ProgressKeptTicks = 5 * TimeSpan.TicksPerSecond;

    private readonly IEmbeddingProvider _provider;
    private readonly IVectorStore _store;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<EmbeddingIndexer> _logger;
    private readonly HashSet<long> _skipped = [];
    private volatile string? _failure;
    private EmbeddingProgress? _progress;
    private long _progressAt;

    public EmbeddingIndexer(IEmbeddingProvider provider, IVectorStore store, IOptions<EmbeddingOptions> options, ILogger<EmbeddingIndexer> logger)
    {
        _provider = provider;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Where the search by meaning is at: off, failed (with the reason) or ready (with the progress).</summary>
    public EmbeddingReport Report(SourceScope? scope = null)
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
            ? new EmbeddingReport(EmbeddingState.Failed, failure, Progress(scope), _provider.ModelId)
            : new EmbeddingReport(EmbeddingState.Ready, null, Progress(scope), _provider.ModelId);
    }

    /// <summary>One pass: computes every chunk that is missing, a batch at a time. Public so that a test can run it. Returns how many were computed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Wanted || _provider.Status().State != EmbeddingState.Ready || !_store.TryPrepare(out _))
        {
            return 0;
        }

        var computed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await Task.Run(() => _store.Missing(_provider.ModelId, _options.BatchSize * Lookahead, _skipped), cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            // The model is the CPU's work: it runs on a thread of the pool, not on the one that serves requests.
            computed += await Task.Run(() => Compute(batch), cancellationToken);
        }

        if (computed > 0)
        {
            LogComputed(computed);
        }

        return computed;
    }

    // A batch that fails is computed again one chunk at a time, and a chunk that still fails is left out until the next start: one passage that the model cannot read (and its
    // id only is logged) must not stop the reading of every passage after it.
    private int Compute(IReadOnlyList<ChunkToEmbed> chunks)
    {
        try
        {
            Embed(chunks);
            return chunks.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && chunks.Count > 1)
        {
            LogBatchFailed(ex);
            return chunks.Sum(chunk => Compute([chunk]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _skipped.Add(chunks[0].ChunkId);
            LogSkipped(chunks[0].ChunkId, ex);
            return 0;
        }
    }

    private void Embed(IReadOnlyList<ChunkToEmbed> chunks)
    {
        var vectors = _provider.EmbedPassages([.. chunks.Select(chunk => chunk.Text)]);
        _store.Save(_provider.ModelId, _provider.Dimension, [.. chunks.Select((chunk, index) => (chunk.ChunkId, vectors[index]))]);
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
            await PassAsync(++passes % Tidyings == 0, stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// One pass of the loop: what is missing is computed, and what is gone is forgotten (after a pass that computed, and now and then). A failure of any kind is kept as the reason
    /// and the loop goes on: it is where everything stops, and an optional feature must not take the host down. The detail is in the log; what is said to the people who ask where
    /// the search is at is not a path of the host. Public so that a test can run it.
    /// </summary>
    public async Task PassAsync(bool tidy, CancellationToken cancellationToken)
    {
        try
        {
            if (await RunAsync(cancellationToken) > 0 || tidy)
            {
                await TidyAsync(cancellationToken);
            }

            _failure = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failure = "The last pass failed: see the log of Mnemarr.";
            LogFailed(ex);
        }
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

    private EmbeddingProgress? Progress(SourceScope? scope)
    {
        try
        {
            // A caller sees the counts of its own sources, computed for it; the probes of the host share one count, kept for a few seconds.
            if (scope is not null)
            {
                return _store.Progress(_provider.ModelId, scope);
            }

            if (_progress is null || Environment.TickCount64 - _progressAt > ProgressKeptTicks / TimeSpan.TicksPerMillisecond)
            {
                _progress = _store.Progress(_provider.ModelId);
                _progressAt = Environment.TickCount64;
            }

            return _progress;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} passages were computed for the search by meaning.")]
    private partial void LogComputed(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "A pass of the search by meaning failed; it is tried again.")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A batch of passages could not be read; they are read one by one.")]
    private partial void LogBatchFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The passage {ChunkId} cannot be read by the model and is left out until the next start.")]
    private partial void LogSkipped(long chunkId, Exception exception);
}
