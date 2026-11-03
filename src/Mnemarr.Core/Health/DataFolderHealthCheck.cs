using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness: the data folder exists and can be written. A missing or read-only volume is the most common way
/// for a container to look alive while it cannot keep anything. The description never reveals the path.
/// </summary>
public sealed class DataFolderHealthCheck : IHealthCheck
{
    private readonly IOptions<MnemarrOptions> _options;

    public DataFolderHealthCheck(IOptions<MnemarrOptions> options)
    {
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var folder = _options.Value.DataFolder;
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
            return HealthCheckResult.Unhealthy("The data folder is not writable.");
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
