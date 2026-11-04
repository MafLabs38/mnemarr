using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Indexing;

/// <summary>
/// The automatic part of the indexing (D-16): at start, then at every interval, each source is brought in line with its folder.
/// A source that fails is logged and the others go on. A folder can change by a pull or by a person, so it is scanned, not watched.
/// </summary>
public sealed partial class SourceIndexingService : BackgroundService
{
    private readonly ISourceStore _sources;
    private readonly SourceIndexer _indexer;
    private readonly IOptions<IndexingOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SourceIndexingService> _logger;
    private readonly Dictionary<Guid, string> _failures = [];

    public SourceIndexingService(
        ISourceStore sources, SourceIndexer indexer, IOptions<IndexingOptions> options, TimeProvider clock, ILogger<SourceIndexingService> logger)
    {
        _sources = sources;
        _indexer = indexer;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass reads disks synchronously for a while: it must not run on the thread that starts the host.
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Value.ScanIntervalSeconds), _clock);
        do
        {
            if (!await ScanOrStopAsync(stoppingToken))
            {
                return;
            }
        }
        while (await WaitForNextAsync(timer, stoppingToken));
    }

    /// <summary>One pass over every source. Public so that a test can run it.</summary>
    public async Task ScanAllAsync(CancellationToken cancellationToken)
    {
        var declared = DeclaredSources();
        _indexer.Retain([.. declared.Select(source => source.Id)]);
        foreach (var source in declared)
        {
            try
            {
                var report = await _indexer.IndexAsync(source, cancellationToken);
                _failures.Remove(source.Id);
                if (report.Added + report.Updated + report.Removed > 0)
                {
                    LogIndexed(source.Name, report.Added, report.Updated, report.Removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A state that lasts (a folder that is not mounted) is said once, then again when it changes or ends: not every interval.
                var what = $"{ex.GetType().Name}: {ex.Message}";
                if (!_failures.TryGetValue(source.Id, out var last) || last != what)
                {
                    _failures[source.Id] = what;
                    LogFailed(source.Name, ex);
                }
            }
        }
    }

    /// <summary>The declared sources, or none while the database cannot be reached: the host stays up and says so on its probes, and the next pass tries again.</summary>
    private IReadOnlyList<Source> DeclaredSources()
    {
        try
        {
            return _sources.List();
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            LogUnavailable(ex);
            return [];
        }
    }

    /// <summary>One pass, and false when the host asked to stop during it: stopping is not a failure, so the service ends cleanly.</summary>
    private async Task<bool> ScanOrStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScanAllAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForNextAsync(PeriodicTimer timer, CancellationToken cancellationToken)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Indexed source {Source}: {Added} added, {Updated} updated, {Removed} removed.")]
    private partial void LogIndexed(string source, int added, int updated, int removed);

    [LoggerMessage(Level = LogLevel.Error, Message = "The database cannot be reached, so no source could be scanned. The next pass tries again.")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Indexing of source {Source} failed.")]
    private partial void LogFailed(string source, Exception exception);
}
