using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness: the data folder exists and can be written. A missing or read-only volume is the most common way
/// for a container to look alive while it cannot keep anything. The description never reveals the path; the
/// exception that caused a failure is kept on the result, for the logs of the operator and not for the caller.
/// The probe writes a file, and the probe endpoint is anonymous: the result is kept for a few seconds, so a client that
/// asks in a loop does not turn into a stream of writes on the disk that holds the data.
/// </summary>
public sealed class DataFolderHealthCheck : IHealthCheck
{
    /// <summary>How long a result is reused. A failing volume is seen again within this time.</summary>
    public static readonly TimeSpan ReuseFor = TimeSpan.FromSeconds(15);

    private readonly IOptions<MnemarrOptions> _options;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private HealthCheckResult _last;
    private DateTimeOffset? _lastAt;

    public DataFolderHealthCheck(IOptions<MnemarrOptions> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_lastAt is { } at && _clock.GetUtcNow() - at < ReuseFor)
            {
                return _last;
            }
        }

        var result = await ProbeAsync(_options.Value.DataFolder, cancellationToken);
        lock (_gate)
        {
            _last = result;
            _lastAt = _clock.GetUtcNow();
        }

        return result;
    }

    private static async Task<HealthCheckResult> ProbeAsync(string folder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            return HealthCheckResult.Unhealthy("The data folder does not exist.");
        }

        var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(probe, [], cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy("The data folder is not writable.", ex);
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A probe file that cannot be removed is not a reason to report the folder as unusable.
        }
    }
}
