using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Backup;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Declarative;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;
using Mnemarr.Core.SystemInfo;
using Mnemarr.Core.Titles;

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
        services.AddOptions<BackupOptions>().Bind(configuration.GetSection("Backup")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BackupOptions>, BackupOptionsValidator>();
        services.AddSingleton<DatabaseBackup>();
        services.AddHostedService<BackupService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<SignInService>();
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<AdminAccountSeeder>();

        services.AddOptions<SourcesOptions>().Bind(configuration.GetSection("Sources")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SourcesOptions>, SourcesOptionsValidator>();
        services.AddOptions<IndexingOptions>().Bind(configuration.GetSection("Indexing")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<IndexingOptions>, IndexingOptionsValidator>();
        services.AddOptions<McpOptions>().Bind(configuration.GetSection("Mcp"));
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
        services.AddSingleton<NoteWriter>();
        services.AddOptions<CollectorOptions>().Bind(configuration.GetSection("Collectors")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<CollectorOptions>, CollectorOptionsValidator>();
        services.AddOptions<IngestOptions>().Bind(configuration.GetSection("Ingest"));
        services.AddSingleton<IMachineStore, SqliteMachineStore>();
        services.AddSingleton<IEnrolmentStore, SqliteEnrolmentStore>();
        services.AddSingleton<EnrolmentThrottle>();
        services.AddSingleton<CollectorDownloads>();
        services.AddSingleton<MachineAccounts>();
        services.AddSingleton<MachineJoining>();
        services.AddSingleton<IPairingStore, SqlitePairingStore>();
        services.AddSingleton<PairingStarts>();
        services.AddSingleton<PairingService>();
        services.AddSingleton<EnrolmentService>();
        services.AddSingleton<CollectorService>();
        services.AddOptions<TitleOptions>().Bind(configuration.GetSection("Titles")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TitleOptions>, TitleOptionsValidator>();
        foreach (var provider in TitleProviders.BuiltIn)
        {
            services.AddSingleton(typeof(ITitleProvider), provider);
        }

        services.TryAddSingleton<ITitleWriter>(sp => TitleProviders.Select(sp.GetServices<ITitleProvider>(), sp.GetRequiredService<IOptions<TitleOptions>>()));
        services.AddSingleton<ConversationTitles>();
        services.AddSingleton<TitleWriting>();
        services.AddHostedService<TitleService>();
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<RawConversations>();
        services.AddSingleton<ConversationIngestor>();
        services.AddHostedService<SourceSyncService>();
        services.AddSingleton<SourceIndexingService>();
        services.AddHostedService(provider => provider.GetRequiredService<SourceIndexingService>());
        return services;
    }
}
