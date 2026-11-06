using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Titles;

/// <summary>Runs <see cref="TitleWriting"/> now and then, when it is turned on.</summary>
public sealed class TitleService : BackgroundService
{
    private readonly TitleWriting _writing;
    private readonly IOptions<TitleOptions> _options;
    private readonly ILogger<TitleService> _logger;

    public TitleService(TitleWriting writing, IOptions<TitleOptions> options, ILogger<TitleService> logger)
    {
        _writing = writing;
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
                var titled = await _writing.RunOnceAsync(stoppingToken);
                if (titled > 0)
                {
                    _logger.LogInformation("{Count} conversation(s) were given a title by the model.", titled);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A pass that fails is tried again at the next: nothing else depends on it.
                _logger.LogWarning(ex, "A pass of the titles failed.");
            }
        }
    }
}
