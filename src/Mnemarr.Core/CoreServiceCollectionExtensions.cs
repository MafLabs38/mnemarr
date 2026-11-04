using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Declarative;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;
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
        services.AddSingleton<SignInService>();
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<AdminAccountSeeder>();

        services.AddOptions<SourcesOptions>().Bind(configuration.GetSection("Sources")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SourcesOptions>, SourcesOptionsValidator>();
        services.AddOptions<IndexingOptions>().Bind(configuration.GetSection("Indexing")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<IndexingOptions>, IndexingOptionsValidator>();
        services.AddOptions<GitOptions>().Bind(configuration.GetSection("Git")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<GitOptions>, GitOptionsValidator>();
        services.AddSingleton<GitCredentials>();
        services.AddSingleton<GitPolicy>();
        services.TryAddSingleton<IGitEngine, LibGit2Engine>();
        services.AddSingleton<SourceLocks>();
        services.AddSingleton<SyncOutcomes>();
        services.AddSingleton<ISourceStore, SqliteSourceStore>();
        services.AddSingleton<INoteIndex, SqliteNoteIndex>();
        services.AddSingleton<INoteBrowser, SqliteNoteBrowser>();
        services.AddSingleton<NoteReader>();
        services.AddSingleton<SourcePaths>();
        services.AddSingleton<SourceCatalog>();
        services.AddSingleton<ConfigurationApplier>();
        services.AddOptions<ConfigurationOptions>().Bind(configuration.GetSection("Config")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ConfigurationOptions>, ConfigurationOptionsValidator>();
        services.AddSingleton<ConfigurationState>();
        services.AddHostedService<ConfigurationFileService>();
        services.AddSingleton<ScanOutcomes>();
        services.AddSingleton<SourceIndexer>();
        services.AddSingleton<SourceSync>();
        services.AddHostedService<SourceSyncService>();
        services.AddSingleton<SourceIndexingService>();
        services.AddHostedService(provider => provider.GetRequiredService<SourceIndexingService>());
        return services;
    }
}
