using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Notifications;

/// <summary>Runs <see cref="AttentionMailer"/> now and then, when notifications are turned on.</summary>
public sealed class AttentionMailService : BackgroundService
{
    private readonly AttentionMailer _mailer;
    private readonly IOptions<NotificationOptions> _options;
    private readonly ILogger<AttentionMailService> _logger;

    public AttentionMailService(AttentionMailer mailer, IOptions<NotificationOptions> options, ILogger<AttentionMailService> logger)
    {
        _mailer = mailer;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Value.PassSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var sent = await _mailer.RunOnceAsync(stoppingToken);
                if (sent > 0)
                {
                    _logger.LogInformation("{Count} mail(s) were sent for machines that ask for attention.", sent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A pass that fails is tried again at the next: nothing else depends on it.
                _logger.LogWarning(ex, "A pass of the notifications failed.");
            }
        }
    }
}
