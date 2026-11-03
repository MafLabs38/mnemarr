using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Health;
using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddMnemarrCore(this IServiceCollection services, IConfiguration configuration)
    {
        // DataFolder stays at the root for compatibility; the settings that come with later verticals get sections of their own.
        services.AddOptions<MnemarrOptions>().Bind(configuration).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MnemarrOptions>, MnemarrOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISystemStatusService, SystemStatusService>();
        services.AddSingleton<DataFolderHealthCheck>(); // one instance: it keeps its last result
        return services;
    }
}
