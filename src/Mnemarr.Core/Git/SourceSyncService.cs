using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Git;

/// <summary>
/// The automatic part of the Git sources (D-16): at start, then at every interval, each Git source is brought up to date and what came in is
/// read. A source that fails is logged when its failure changes, and the others go on: a host that is down is said on the source and on
/// readiness, not at every interval in the log.
/// </summary>
public sealed partial class SourceSyncService : BackgroundService
{
    private readonly ISourceStore _sources;
    private readonly SourceSync _sync;
    private readonly SyncOutcomes _outcomes;
    private readonly IOptions<GitOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SourceSyncService> _logger;
    private readonly Dictionary<Guid, string> _failures = [];

    public SourceSyncService(ISourceStore sources, SourceSync sync, SyncOutcomes outcomes, IOptions<GitOptions> options, TimeProvider clock, ILogger<SourceSyncService> logger)
    {
        _sources = sources;
        _sync = sync;
        _outcomes = outcomes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first sync clones, which takes a while and reads the network: not on the thread that starts the host.
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Value.SyncIntervalSeconds), _clock);
        do
        {
            if (!await SyncOrStopAsync(stoppingToken))
            {
                return;
            }
        }
        while (await WaitForNextAsync(timer, stoppingToken));
    }

    /// <summary>One pass over every Git source. Public so that a test can run it.</summary>
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        var declared = Declared();
        var ids = declared.Select(source => source.Id).ToHashSet();
        _outcomes.Retain([.. ids]);
        foreach (var gone in _failures.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _failures.Remove(gone);
        }

        foreach (var source in declared.Where(source => source.Repository is not null))
        {
            try
            {
                await _sync.SyncAsync(source, waitForTurn: true, cancellationToken);
                _failures.Remove(source.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var what = ex is RejectedException ? _outcomes.Of(source.Id)?.Error ?? "refused" : ex.GetType().Name;
                if (!_failures.TryGetValue(source.Id, out var last) || last != what)
                {
                    _failures[source.Id] = what;
                    LogFailed(source.Name, what);
                }
            }
        }
    }

    private IReadOnlyList<Source> Declared()
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

    private async Task<bool> SyncOrStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SyncAllAsync(cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sync of the Git source {Source} failed: {Reason}")]
    private partial void LogFailed(string source, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The database cannot be reached, so no Git source could be synchronised. The next pass tries again.")]
    private partial void LogUnavailable(Exception exception);
}
