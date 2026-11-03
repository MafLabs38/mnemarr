using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Accounts;

/// <summary>Forgets the events of the audit log older than <see cref="AuthOptions.AuditRetentionDays"/>: at start, then every six hours.</summary>
public sealed partial class AuditRetentionService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);
    private readonly IAuditLog _audit;
    private readonly IOptions<AuthOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditRetentionService> _logger;

    public AuditRetentionService(IAuditLog audit, IOptions<AuthOptions> options, TimeProvider clock, ILogger<AuditRetentionService> logger)
    {
        _audit = audit;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start does not wait for the first pass: a slow or locked database must not hold the host back.
        await Task.Yield();
        using var timer = new PeriodicTimer(Every, _clock);
        do
        {
            Prune();
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One pass. Public so that a test can run it.</summary>
    public void Prune()
    {
        try
        {
            var removed = _audit.Prune(_clock.GetUtcNow().AddDays(-_options.Value.AuditRetentionDays));
            if (removed > 0)
            {
                LogPruned(removed);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or DirectoryNotFoundException)
        {
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Forgot {Count} events of the audit log that were past their retention.")]
    private partial void LogPruned(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The audit log could not be pruned.")]
    private partial void LogFailed(Exception exception);
}
