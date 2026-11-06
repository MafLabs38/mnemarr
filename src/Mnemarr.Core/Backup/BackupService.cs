using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mnemarr.Core.Backup;

/// <summary>Keeps the snapshots of the database (ADR 0029): one at start if it is due, then it looks every hour whether the next is.</summary>
public sealed partial class BackupService : BackgroundService
{
    private static readonly TimeSpan Look = TimeSpan.FromHours(1);
    private readonly DatabaseBackup _backup;
    private readonly TimeProvider _clock;
    private readonly ILogger<BackupService> _logger;

    public BackupService(DatabaseBackup backup, TimeProvider clock, ILogger<BackupService> logger)
    {
        _backup = backup;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_backup.Enabled)
        {
            return;
        }

        // Start does not wait for the first snapshot: a slow or locked database must not hold the host back.
        await Task.Yield();
        using var timer = new PeriodicTimer(Look, _clock);
        do
        {
            TakeIfDue();
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One look: a snapshot is taken if it is due.</summary>
    internal void TakeIfDue()
    {
        try
        {
            if (_backup.IsDue())
            {
                LogTaken(_backup.Snapshot());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tried again at the next look; a snapshot that fails must be seen in the log, and must not stop anything else.
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

    [LoggerMessage(Level = LogLevel.Information, Message = "A snapshot of the database was taken: {Path}.")]
    private partial void LogTaken(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "A snapshot of the database could not be taken.")]
    private partial void LogFailed(Exception exception);
}
