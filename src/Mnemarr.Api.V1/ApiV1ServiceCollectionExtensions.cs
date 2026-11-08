using Microsoft.Extensions.DependencyInjection;
using Mnemarr.Api.V1.Accounts;
using Mnemarr.Api.V1.SystemInfo;

namespace Mnemarr.Api.V1;

public static class ApiV1ServiceCollectionExtensions
{
    /// <summary>Registers the controllers of <c>/api/v1</c>, found in this assembly.</summary>
    public static IServiceCollection AddMnemarrApiV1(this IServiceCollection services)
    {
        services.AddSingleton<Collectors.InstallCommands>();
        services.AddSingleton<Mcp.McpAdminTools>();
        services.AddSingleton<Mcp.McpTools>();
        // The runner is a sibling container whose address can change when it is recreated: connections are renewed every two minutes.
        services.AddHttpClient<Chat.AgentRunnerClient>().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
        services.AddSingleton<Chat.ChatService>();
        services.AddMnemarrRateLimits();
        services.AddControllers(options => options.Filters.Add<RejectedFilter>()).AddApplicationPart(typeof(SystemController).Assembly);
        return services;
    }
}
