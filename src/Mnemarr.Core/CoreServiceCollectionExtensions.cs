using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Storage;
using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddMnemarrCore(this IServiceCollection services, IConfiguration configuration)
    {
        // DataFolder stays at the root for compatibility; the settings that come with later verticals get sections of their own.
        services.AddSingleton<IValidateOptions<MnemarrOptions>, MnemarrOptionsValidator>();
        services.AddOptions<MnemarrOptions>().Bind(configuration).ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISystemStatusService, SystemStatusService>();

        services.AddOptions<AuthOptions>().Bind(configuration.GetSection("Auth")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddOptions<AuditOptions>().Bind(configuration.GetSection("Audit")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuditOptions>, AuditOptionsValidator>();
        services.AddSingleton<IDatabase, Database>();
        services.AddSingleton<IAccountStore, SqliteAccountStore>();
        services.AddSingleton<IApiKeyStore, SqliteApiKeyStore>();
        services.AddSingleton<IAuditLog, SqliteAuditLog>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<AuditSampler>();
        services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
        services.AddSingleton<ISessionStore, SqliteSessionStore>();
        services.AddSingleton<SessionService>();
        services.AddHostedService<AuditRetentionService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<AdminAccountSeeder>();
        return services;
    }
}
