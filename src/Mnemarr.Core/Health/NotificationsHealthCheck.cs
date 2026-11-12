using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Notifications;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the notifications (ADR 0042): when they are on and cannot work (the secret cannot be read, or the last mail did not leave), it is degraded with the reason, never unhealthy.
/// Off, it is always healthy.
/// </summary>
public sealed class NotificationsHealthCheck : IHealthCheck
{
    private readonly IOptions<NotificationOptions> _options;
    private readonly IEnumerable<INotificationProvider> _providers;
    private readonly INotificationLog _log;

    public NotificationsHealthCheck(IOptions<NotificationOptions> options, IEnumerable<INotificationProvider> providers, INotificationLog log)
    {
        _options = options;
        _providers = providers;
        _log = log;
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

        if (NotificationProviders.Find(_providers, options.Provider)?.CheckSecrets(options) is { } secret)
        {
            return HealthReasons.Degraded("notifications-secret", secret);
        }

        return _log.Last() is { Delivered: false } failed
            ? HealthReasons.Degraded("notifications-failing", $"The last mail could not be sent: {failed.Reason}")
            : HealthCheckResult.Healthy();
    }
}
