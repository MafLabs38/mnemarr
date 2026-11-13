using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Backup;
using Mnemarr.Core.Chat;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Consolidation;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Declarative;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Git;
using Mnemarr.Core.Identity;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Notifications;
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
        services.AddOptions<BrandingOptions>().Bind(configuration.GetSection(BrandingOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BrandingOptions>, BrandingOptionsValidator>();
        services.AddSingleton<IIdentityStore, SqliteIdentityStore>();
        services.AddSingleton<IdentityService>();
        services.AddOptions<AgentOptions>().Bind(configuration.GetSection(AgentOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AgentOptions>, AgentOptionsValidator>();
        services.AddSingleton<ChatAccess>();
        services.AddOptions<ConsolidationOptions>().Bind(configuration.GetSection("Consolidation")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ConsolidationOptions>, ConsolidationOptionsValidator>();
        services.AddSingleton<IConsolidationStore, SqliteConsolidationStore>();
        services.AddSingleton<ConsolidationAccount>();
        services.AddSingleton<ConsolidationInput>();
        services.AddSingleton<Context.ContextPackBuilder>();

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
        services.AddSingleton<EmbeddingWakeUp>();
        services.AddSingleton<SqliteNoteIndex>();
        services.AddSingleton<INoteIndex>(sp => sp.GetRequiredService<SqliteNoteIndex>());
        services.AddSingleton<IChunkHits>(sp => sp.GetRequiredService<SqliteNoteIndex>());
        services.AddOptions<EmbeddingOptions>().Bind(configuration.GetSection("Embeddings")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmbeddingOptions>, EmbeddingOptionsValidator>();
        services.AddSingleton<OnnxEmbeddingProvider>();
        // No redirect is followed: the key of the server must not be sent to another address. The client is the one of this singleton, which lives as long as the host.
        services.AddSingleton(sp => new RemoteEmbeddingClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            {
                // The client times each call itself (Remote:TimeoutSeconds, up to 300); an answer is a few kilobytes of numbers, so a server cannot make the pod buffer more than this.
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = 16 * 1024 * 1024,
            }, sp.GetRequiredService<IOptions<EmbeddingOptions>>()));
        services.TryAddSingleton<IEmbeddingProvider>(sp =>
            sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Remote.Wanted
                ? ActivatorUtilities.CreateInstance<FailoverEmbeddingProvider>(sp, (IEmbeddingProvider)sp.GetRequiredService<OnnxEmbeddingProvider>())
                : sp.GetRequiredService<OnnxEmbeddingProvider>());
        services.TryAddSingleton<IVectorStore, SqliteVectorStore>();
        services.AddSingleton<EmbeddingIndexer>();
        services.AddHostedService(sp => sp.GetRequiredService<EmbeddingIndexer>());
        services.AddSingleton<NoteSearch>();
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
        services.AddSingleton<CollectorUpdates>();
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
        services.AddOptions<NotificationOptions>().Bind(configuration.GetSection("Notifications")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<NotificationOptions>, NotificationOptionsValidator>();
        foreach (var provider in NotificationProviders.BuiltIn)
        {
            services.AddSingleton(typeof(INotificationProvider), provider);
        }

        services.TryAddSingleton<INotificationSender>(sp => NotificationProviders.Select(sp.GetServices<INotificationProvider>(), sp.GetRequiredService<IOptions<NotificationOptions>>()));
        services.AddSingleton<INotificationLog, SqliteNotificationLog>();
        services.AddSingleton<AttentionMailer>();
        services.AddHostedService<AttentionMailService>();
        services.AddSingleton<ConversationTitles>();
        services.AddSingleton<TitleWriting>();
        services.AddHostedService<TitleService>();
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<RawConversations>();
        services.AddSingleton<ConversationRelocator>();
        services.AddSingleton<ConversationIngestor>();
        services.AddHostedService<SourceSyncService>();
        services.AddSingleton<SourceIndexingService>();
        services.AddHostedService(provider => provider.GetRequiredService<SourceIndexingService>());
        return services;
    }
}
