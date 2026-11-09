using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mnemarr.Core.Health;

namespace Mnemarr.Host.Health;

public static class HealthExtensions
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddMnemarrHealthChecks(this IServiceCollection services)
    {
        // One instance: it keeps its last result, and AddCheck would otherwise build a new one for every probe.
        services.TryAddSingleton<DataFolderHealthCheck>();
        services.TryAddSingleton<Mnemarr.Api.V1.Chat.AgentHealthCheck>();
        services.TryAddSingleton<Mnemarr.Api.V1.Consolidation.ConsolidationHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<DataFolderHealthCheck>("data-folder", tags: [ReadyTag])
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
            .AddCheck<AccountsHealthCheck>("accounts", tags: [ReadyTag])
            .AddCheck<SourcesHealthCheck>("sources", tags: [ReadyTag])
            .AddCheck<ConfigurationHealthCheck>("configuration", tags: [ReadyTag])
            .AddCheck<CollectorsHealthCheck>("collectors", tags: [ReadyTag])
            .AddCheck<BackupHealthCheck>("backup", tags: [ReadyTag])
            .AddCheck<EmbeddingsHealthCheck>("embeddings", tags: [ReadyTag])
            .AddCheck<Mnemarr.Api.V1.Chat.AgentHealthCheck>("chat", tags: [ReadyTag])
            .AddCheck<Mnemarr.Api.V1.Consolidation.ConsolidationHealthCheck>("consolidation", tags: [ReadyTag]);
        return services;
    }

    /// <summary>
    /// <c>/health/live</c>: the process answers (no check, for a liveness probe).
    /// <c>/health/ready</c>: it can also do its job (the data folder is writable, for a readiness probe).
    /// </summary>
    public static IEndpointRouteBuilder MapMnemarrHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(ReadyTag) }).AllowAnonymous();
        return endpoints;
    }
}
