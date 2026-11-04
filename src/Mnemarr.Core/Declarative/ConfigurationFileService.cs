using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Declarative;

/// <summary>
/// Configuration as code, from a file (ADR 0020): at start, then whenever the file changes (a mounted ConfigMap, a pull), the document is
/// applied. A file that cannot be applied does not stop the application: the reason is on the readiness probe, and the next pass tries
/// again, so that a folder that is mounted late is found.
/// </summary>
public sealed partial class ConfigurationFileService : BackgroundService
{
    private readonly ConfigurationApplier _applier;
    private readonly ConfigurationState _state;
    private readonly IOptions<ConfigurationOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConfigurationFileService> _logger;

    public ConfigurationFileService(
        ConfigurationApplier applier, ConfigurationState state, IOptions<ConfigurationOptions> options, TimeProvider clock, ILogger<ConfigurationFileService> logger)
    {
        _applier = applier;
        _state = state;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.File))
        {
            return;
        }

        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Value.ReloadSeconds), _clock);
        do
        {
            if (!await ApplyOrStopAsync(stoppingToken))
            {
                return;
            }
        }
        while (await WaitForNextAsync(timer, stoppingToken));
    }

    /// <summary>One look at the file. A file that was applied and has not changed is left alone; one that failed is tried again.</summary>
    public async Task ApplyOnceAsync(CancellationToken cancellationToken)
    {
        var file = _options.Value.File;
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        if (await ReadAsync(file, cancellationToken) is not { } text)
        {
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (hash == _state.AppliedHash && _state.Error is null)
        {
            return;
        }

        try
        {
            var plan = _applier.Apply(ConfigurationYaml.Parse(text), AuditActors.System);
            if (plan.Valid)
            {
                _state.Applied(hash);
                LogApplied(plan.Items.Count(item => item.Action != PlanAction.Keep));
            }
            else
            {
                Fail(string.Join(" ", plan.Errors));
            }
        }
        catch (RejectedException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            Fail("The database cannot be reached, so the configuration could not be applied.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever else goes wrong with one document is that document's failure, on the probe: it does not stop the host.
            LogUnexpected(ex);
            Fail("The configuration could not be applied.");
        }
    }

    /// <summary>The file, or null (and the reason in the state) when it cannot be read: a folder that is not mounted yet is not a reason to stop.</summary>
    private async Task<string?> ReadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(file).Length > ConfigurationYaml.MaxBytes)
            {
                Fail($"The configuration file is larger than {ConfigurationYaml.MaxBytes / 1024} KB.");
                return null;
            }

            return await File.ReadAllTextAsync(file, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("The configuration file cannot be read.");
            return null;
        }
    }

    private void Fail(string reason)
    {
        // A file that stays wrong is said once, not at every look.
        var repeated = _state.Error == reason;
        _state.Failed(reason);
        if (!repeated)
        {
            LogFailed(reason);
        }
    }

    /// <summary>One look, and false when the host asked to stop during it: stopping is not a failure.</summary>
    private async Task<bool> ApplyOrStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ApplyOnceAsync(cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "The configuration file was applied: {Changes} change(s).")]
    private partial void LogApplied(int changes);

    [LoggerMessage(Level = LogLevel.Error, Message = "The configuration file could not be applied because of an unexpected error.")]
    private partial void LogUnexpected(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The configuration file was not applied: {Reason}")]
    private partial void LogFailed(string reason);
}
