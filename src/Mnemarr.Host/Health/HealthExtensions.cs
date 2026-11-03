using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Mnemarr.Core.Health;

namespace Mnemarr.Host.Health;

public static class HealthExtensions
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddMnemarrHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DataFolderHealthCheck>("data-folder", tags: [ReadyTag]);
        return services;
    }

    /// <summary>
    /// <c>/health/live</c>: the process answers (no check, for a liveness probe).
    /// <c>/health/ready</c>: it can also do its job (the data folder is writable, for a readiness probe).
    /// </summary>
    public static IEndpointRouteBuilder MapMnemarrHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(ReadyTag) });
        return endpoints;
    }
}
