using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Backup;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the snapshots of the database (ADR 0029): Mnemarr is degraded, not out of service, when they have stopped, because a backup that stops
/// without a sign is how data is lost. The reason says whether the attempts fail (<c>backup-failing</c>) or none has been made for too long (<c>backup-late</c>).
/// </summary>
public sealed class BackupHealthCheck : IHealthCheck
{
    private readonly DatabaseBackup _backup;

    public BackupHealthCheck(DatabaseBackup backup) => _backup = backup;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_backup.IsLate())
        {
            return Task.FromResult(HealthCheckResult.Healthy());
        }

        return Task.FromResult(_backup.FailingSince is { } since
            ? HealthReasons.Degraded("backup-failing", $"A snapshot of the database could not be taken since {since:yyyy-MM-dd HH:mm} UTC: see the log.")
            : HealthReasons.Degraded("backup-late", "No snapshot of the database has been taken for more than twice the usual interval."));
    }
}
