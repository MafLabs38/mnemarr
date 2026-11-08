using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Consolidation;

namespace Mnemarr.Api.V1.Consolidation;

/// <summary>Runs the consolidation now and then, when it is turned on (ADR 0036).</summary>
public sealed class ConsolidationService : BackgroundService
{
    private readonly Consolidator _consolidator;
    private readonly IOptions<ConsolidationOptions> _options;
    private readonly ILogger<ConsolidationService> _logger;

    public ConsolidationService(Consolidator consolidator, IOptions<ConsolidationOptions> options, ILogger<ConsolidationService> logger)
    {
        _consolidator = consolidator;
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
                var asked = await _consolidator.RunPassAsync(ignoreIdle: false, stoppingToken);
                if (asked > 0)
                {
                    _logger.LogInformation("{Count} conversation(s) were consolidated into notes.", asked);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A pass that fails is tried again at the next: nothing else depends on it.
                _logger.LogWarning(ex, "A pass of the consolidation failed.");
            }
        }
    }
}
