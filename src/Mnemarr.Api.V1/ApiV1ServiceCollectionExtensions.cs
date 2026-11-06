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
        services.AddSingleton<Mcp.McpTools>();
        services.AddMnemarrRateLimits();
        services.AddControllers(options => options.Filters.Add<RejectedFilter>()).AddApplicationPart(typeof(SystemController).Assembly);
        return services;
    }
}
