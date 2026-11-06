using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Backup;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the snapshots of the database (ADR 0029): Mnemarr is degraded, not out of service, when they fail or have stopped, because a backup that stops
/// without a sign is how data is lost. The reason says whether the attempts fail (<c>backup-failing</c>) or none has been made for too long (<c>backup-late</c>).
/// </summary>
public sealed class BackupHealthCheck : IHealthCheck
{
    private readonly DatabaseBackup _backup;

    public BackupHealthCheck(DatabaseBackup backup) => _backup = backup;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_backup.FailingSince is { } since && _backup.IsFailing)
            {
                return Task.FromResult(HealthReasons.Degraded("backup-failing", $"A snapshot of the database could not be taken since {since:yyyy-MM-dd HH:mm} UTC: see the log."));
            }

            return Task.FromResult(_backup.IsLate()
                ? HealthReasons.Degraded("backup-late", "No snapshot of the database has been taken for more than twice the usual interval.")
                : HealthCheckResult.Healthy());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder of the snapshots cannot be read (its modes, a disk that failed): that is a backup that is not working.
            return Task.FromResult(HealthReasons.Degraded("backup-failing", "The folder of the snapshots of the database cannot be read."));
        }
    }
}
