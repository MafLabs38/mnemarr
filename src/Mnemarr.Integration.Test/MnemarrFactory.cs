using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Git;

namespace Mnemarr.Integration.Test;

/// <summary>The real application in memory, with its own data folder (created or not) that is removed afterwards.</summary>
public abstract class MnemarrFactoryBase : WebApplicationFactory<Program>
{
    private readonly bool _createFolder;

    protected MnemarrFactoryBase(bool createFolder, string? existingFolder = null)
    {
        // A host started again on the folder of another one does not own it: whoever made it removes it.
        _createFolder = createFolder && existingFolder is null;
        DataFolder = existingFolder ?? Path.Combine(Path.GetTempPath(), $"mnemarr-test-{Guid.NewGuid():N}");
        if (_createFolder)
        {
            Directory.CreateDirectory(DataFolder);
        }
    }

    public string DataFolder { get; }

    /// <summary>The cookie of the test hosts is not <c>Secure</c> (they speak plain HTTP) unless a test asks for the default.</summary>
    protected virtual CookieSecurityMode CookieSecurity => CookieSecurityMode.SameAsRequest;

    /// <summary>How long a session lasts at most; a test of the absolute lifetime shortens it instead of living through a month.</summary>
    protected virtual int SessionMaxDays => 30;

    /// <summary>The configuration document to give the host as a file (configuration as code), written when the host is configured; none by default.</summary>
    protected virtual string? ConfigurationText => null;

    /// <summary>The password that a break-glass reset gives to <see cref="AdminName"/> at start, when a test restarts a host with one.</summary>
    protected virtual string? ResetPassword => null;

    /// <summary>Whether a key may send only for the machine it was enrolled for. Off for the tests that make their own collector accounts; on for the tests of the enrolment itself.</summary>
    protected virtual bool RequireEnrolment => false;

    /// <summary>The folder of programs the host serves to machines that join (none by default).</summary>
    protected virtual string? DownloadFolder => null;

    /// <summary>The folder of the feed of updates of the collector, when it is not <c>updates</c> in the folder of the programs.</summary>
    protected virtual string? UpdateFolder => null;

    /// <summary>The address machines are told to reach the host at, when it is not the one of the request.</summary>
    protected virtual string? PublicUrl => null;

    /// <summary>Whether the host starts with a first administrator. A test of an empty Mnemarr turns it off.</summary>
    protected virtual bool HasAdministrator => true;

    /// <summary>The administrator every test host starts with, created from the settings like the first run of a real one.</summary>
    public const string AdminName = "admin";

    public const string AdminPassword = "integration-test-password";

    private readonly string _passwordFile = Path.Combine(Path.GetTempPath(), $"mnemarr-admin-{Guid.NewGuid():N}");
    /// <summary>Where the configuration document is, for a host that has one.</summary>
    public string ConfigurationFile { get; } = Path.Combine(Path.GetTempPath(), $"mnemarr-config-{Guid.NewGuid():N}.yaml");

    private readonly string _resetFile = Path.Combine(Path.GetTempPath(), $"mnemarr-reset-{Guid.NewGuid():N}");

    /// <summary>The Git engine of the host: nothing is cloned in a test, and the test says what a sync does.</summary>
    public FakeGitEngine Git { get; } = new();

    /// <summary>The application's clock, so that a test can let time pass without waiting.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DataFolder", DataFolder);
        File.WriteAllText(_passwordFile, AdminPassword);
        if (ConfigurationText is { } configuration)
        {
            File.WriteAllText(ConfigurationFile, configuration);
        }
        if (ResetPassword is { } reset)
        {
            File.WriteAllText(_resetFile, reset);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            // Applied after every source, the MNEMARR__ variables of the machine included.
            services.PostConfigure<MnemarrOptions>(options => options.DataFolder = DataFolder);
            services.AddSingleton<IGitEngine>(Git);
            services.PostConfigure<GitOptions>(options =>
            {
                options.Hosts = ["github.example"];
                options.Credentials["github"] = new GitCredentialOptions { Host = "github.example", Token = "secret-token-do-not-leak" };
            });
            services.PostConfigure<Mnemarr.Core.Declarative.ConfigurationOptions>(options =>
            {
                options.File = ConfigurationText is null ? null : ConfigurationFile;
                options.ReloadSeconds = 1;
            });
            services.PostConfigure<Mnemarr.Core.Collectors.IngestOptions>(options => options.RequireEnrolment = RequireEnrolment);
            services.PostConfigure<Mnemarr.Core.Collectors.CollectorOptions>(options =>
            {
                options.DownloadFolder = DownloadFolder;
                options.UpdateFolder = UpdateFolder;
                options.PublicUrl = PublicUrl;
            });
            services.PostConfigure<AuthOptions>(options =>
            {
                options.AdminUser = HasAdministrator ? AdminName : null;
                options.AdminPasswordFile = HasAdministrator ? _passwordFile : null;
                options.CookieSecurity = CookieSecurity;
                options.SessionMaxDays = SessionMaxDays;
                options.ResetPasswordUser = ResetPassword is null ? null : AdminName;
                options.ResetPasswordFile = ResetPassword is null ? null : _resetFile;
            });
            ConfigureServices(services);
        });
    }

    /// <summary>Replaces services of the application for one kind of test.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    /// <summary>A client that behaves like the interface: it keeps the session cookie and sends the header a write needs.</summary>
    public HttpClient CreateBrowserClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Mnemarr-Request", "1");
        return client;
    }

    /// <summary>A browser client that has signed in.</summary>
    public async Task<HttpClient> SignedInClientAsync(string name = AdminName, string password = AdminPassword)
    {
        var client = CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { name, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A client that sends an API key, as an agent does.</summary>
    public HttpClient CreateKeyClient(string secret)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            File.Delete(_passwordFile);
            File.Delete(_resetFile);
            File.Delete(ConfigurationFile);
        }

        if (disposing && _createFolder && Directory.Exists(DataFolder))
        {
            Directory.Delete(DataFolder, recursive: true);
        }
    }
}

/// <summary>An application whose data folder exists and is writable.</summary>
public sealed class MnemarrFactory : MnemarrFactoryBase
{
    public MnemarrFactory()
        : base(createFolder: true)
    {
    }
}

/// <summary>An application that, like a real one, lets a key send only for the machine it was enrolled for.</summary>
public sealed class EnrolledOnlyFactory : MnemarrFactoryBase
{
    public EnrolledOnlyFactory()
        : base(createFolder: true)
    {
    }

    protected override bool RequireEnrolment => true;
}

/// <summary>A model that gives what a test says: titles written by a model, without a network.</summary>
public sealed class FakeTitleWriter : Mnemarr.Core.Titles.ITitleWriter
{
    public List<string> Asked { get; } = [];

    public Task<Mnemarr.Core.Titles.GeneratedTitle?> WriteAsync(string transcript, CancellationToken cancellationToken)
    {
        Asked.Add(transcript);
        return Task.FromResult<Mnemarr.Core.Titles.GeneratedTitle?>(new Mnemarr.Core.Titles.GeneratedTitle("The model's title", "The model's summary."));
    }
}

/// <summary>An application where a model writes the titles of conversations: a fake one, and a background pass that never comes by itself during a test.</summary>
public sealed class TitlesFactory : MnemarrFactoryBase
{
    public TitlesFactory()
        : base(createFolder: true)
    {
    }

    public FakeTitleWriter Writer { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<Mnemarr.Core.Titles.ITitleWriter>(Writer);
        services.PostConfigure<Mnemarr.Core.Titles.TitleOptions>(options =>
        {
            options.Enabled = true;
            options.ApiKeyFile = "/not-read-by-the-fake";
            options.PassSeconds = 3600;
            options.IdleMinutes = 1440;
        });
    }
}

/// <summary>An application that serves the programs a machine installs: fake files, so that the checksums can be told.</summary>
public class DownloadsFactory : MnemarrFactoryBase
{
    private readonly string _downloads = Path.Combine(Path.GetTempPath(), $"mnemarr-dl-{Guid.NewGuid():N}");

    public DownloadsFactory()
        : base(createFolder: true)
    {
        Directory.CreateDirectory(_downloads);
        File.WriteAllText(Path.Combine(_downloads, "mnemarr-collector-windows-x86_64.exe"), "the windows program");
        File.WriteAllText(Path.Combine(_downloads, "mnemarr-collector-linux-x86_64"), "the linux program");
        File.WriteAllText(Path.Combine(_downloads, "not-served.txt"), "a file that is not one of the programs");
    }

    protected override bool RequireEnrolment => true;

    protected override string? DownloadFolder => _downloads;

    /// <summary>What the checksum of a fake program is.</summary>
    public static string Sha256Of(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_downloads))
        {
            Directory.Delete(_downloads, recursive: true);
        }
    }
}

/// <summary>The programs, with a Windows one big enough that a client that does not read it keeps the server writing it.</summary>
public sealed class BigProgramFactory : DownloadsFactory
{
    public BigProgramFactory()
    {
        File.WriteAllBytes(Path.Combine(DownloadFolder!, "mnemarr-collector-windows-x86_64.exe"), new byte[8 * 1024 * 1024]);
    }
}

/// <summary>The same, behind a proxy that ends TLS: the address given to machines is set, and is https.</summary>
public sealed class PublicUrlFactory : DownloadsFactory
{
    protected override string? PublicUrl => "https://mnemarr.homelab.example";
}

/// <summary>An application whose data folder does not exist, as with a volume that was never mounted.</summary>
public sealed class MissingDataFolderFactory : MnemarrFactoryBase
{
    public MissingDataFolderFactory()
        : base(createFolder: false)
    {
    }
}

/// <summary>An application whose status service fails, to see what an unhandled error looks like from outside.</summary>
public sealed class FailingStatusFactory : MnemarrFactoryBase
{
    public FailingStatusFactory()
        : base(createFolder: true)
    {
    }

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<Mnemarr.Core.SystemInfo.ISystemStatusService, ThrowingStatusService>();

    private sealed class ThrowingStatusService : Mnemarr.Core.SystemInfo.ISystemStatusService
    {
        public Mnemarr.Core.SystemInfo.SystemStatus GetStatus() => throw new InvalidOperationException("The status could not be read.");
    }
}

/// <summary>An application that keeps the default of the cookie, which is <c>Secure</c>.</summary>
public sealed class SecureCookieFactory : MnemarrFactoryBase
{
    public SecureCookieFactory()
        : base(createFolder: true)
    {
    }

    protected override CookieSecurityMode CookieSecurity => CookieSecurityMode.Always;
}

/// <summary>An application that starts without a first administrator: nobody can sign in.</summary>
public sealed class NoAdministratorFactory : MnemarrFactoryBase
{
    public NoAdministratorFactory()
        : base(createFolder: true)
    {
    }

    protected override bool HasAdministrator => false;
}

/// <summary>An application started again on the data folder of another one: what a restart of a real instance is.</summary>
public sealed class RestartedFactory : MnemarrFactoryBase
{
    private readonly string? _resetPassword;

    public RestartedFactory(MnemarrFactory first, string? resetPassword = null)
        : base(createFolder: true, existingFolder: first.DataFolder)
    {
        _resetPassword = resetPassword;
    }

    protected override string? ResetPassword => _resetPassword;
}

/// <summary>An application whose sessions last one day at most.</summary>
public sealed class ShortSessionFactory : MnemarrFactoryBase
{
    public ShortSessionFactory()
        : base(createFolder: true)
    {
    }

    protected override int SessionMaxDays => 1;
}

/// <summary>
/// An application with one more call, <c>/probe/scope</c>, that answers what the scope of the caller is: the claims of a key are rebuilt by the
/// real authentication, and read the way a handler of a later vertical will read them.
/// </summary>
public sealed class ScopeProbeFactory : MnemarrFactoryBase
{
    public ScopeProbeFactory()
        : base(createFolder: true)
    {
    }

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, ProbeFilter>();

    private sealed class ProbeFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            Microsoft.AspNetCore.Builder.MapExtensions.Map(app, "/probe/scope", branch => Microsoft.AspNetCore.Builder.RunExtensions.Run(branch, async context =>
            {
                var result = await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(context);
                var scope = (result.Principal ?? new System.Security.Claims.ClaimsPrincipal()).Scope();
                context.Response.ContentType = "application/json";
                await Microsoft.AspNetCore.Http.HttpResponseJsonExtensions.WriteAsJsonAsync(context.Response, new { all = scope.Sources is null, sources = scope.Sources ?? [] });
            }));
            next(app);
        };
    }
}

/// <summary>An application given a configuration document as a file, which it applies at start: a memory vault and a knowledge vault.</summary>
public sealed class ConfigFileFactory : MnemarrFactoryBase
{
    public ConfigFileFactory()
        : base(createFolder: true)
    {
        Directory.CreateDirectory(Path.Combine(DataFolder, "sources", "memory"));
        Directory.CreateDirectory(Path.Combine(DataFolder, "sources", "cours"));
    }

    protected override string? ConfigurationText =>
        $"memory:\n  path: {Path.Combine(DataFolder, "sources", "memory")}\nknowledge:\n  - name: cours\n    path: {Path.Combine(DataFolder, "sources", "cours")}\n";
}

/// <summary>An application whose operator turned the MCP write tools on (<c>Mcp:WriteTools</c>).</summary>
public sealed class McpWriteFactory : MnemarrFactoryBase
{
    public McpWriteFactory()
        : base(createFolder: true)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Mcp:WriteTools", "true");
        builder.UseSetting("Mcp:SourceTools", "true");
    }
}

/// <summary>An application whose operator turned the MCP write tools on for notes only (<c>Mcp:WriteTools</c>, not <c>Mcp:SourceTools</c>).</summary>
public sealed class McpNotesOnlyFactory : MnemarrFactoryBase
{
    public McpNotesOnlyFactory()
        : base(createFolder: true)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Mcp:WriteTools", "true");
    }
}

/// <summary>A host with a built frontend: a page, and a script whose name carries a hash.</summary>
public sealed class FrontendFactory : MnemarrFactoryBase
{
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), $"mnemarr-web-{Guid.NewGuid():N}");

    public FrontendFactory()
        : base(createFolder: true)
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>Mnemarr</title>");
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-AbC123.js"), "console.log(1)");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseWebRoot(_webRoot);
        base.ConfigureWebHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_webRoot))
        {
            Directory.Delete(_webRoot, recursive: true);
        }
    }
}

/// <summary>A host that carries the updates of the collector (ADR 0032): a feed of two versions, their packages and an installer, and files that are not any of those.</summary>
public sealed class UpdatesFactory : DownloadsFactory
{
    public const string Latest = "1.4.0";
    private readonly string _updates;

    public UpdatesFactory()
    {
        _updates = Path.Combine(DownloadFolder!, Mnemarr.Core.Collectors.CollectorUpdates.FolderName);
        Directory.CreateDirectory(_updates);
        File.WriteAllText(Path.Combine(_updates, "releases.win.json"), """{"Assets":[{"PackageId":"MnemarrCollector","Version":"1.3.0","Type":"Full","FileName":"MnemarrCollector-1.3.0-full.nupkg","SHA1":"A","SHA256":"B","Size":10},{"PackageId":"MnemarrCollector","Version":"1.4.0","Type":"Full","FileName":"MnemarrCollector-1.4.0-full.nupkg","SHA1":"C","SHA256":"D","Size":12},{"PackageId":"MnemarrCollector","Version":"1.4.0","Type":"Delta","FileName":"MnemarrCollector-1.4.0-delta.nupkg","SHA1":"E","SHA256":"F","Size":3}]}""");
        File.WriteAllText(Path.Combine(_updates, "MnemarrCollector-1.4.0-full.nupkg"), "the package of 1.4.0");
        File.WriteAllText(Path.Combine(_updates, "MnemarrCollector-win-Setup.exe"), "the installer");
        File.WriteAllText(Path.Combine(_updates, "notes.txt"), "a file that is not one of those the installer makes");
        File.WriteAllText(Path.Combine(_updates, "secrets.json"), "{}");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_updates))
        {
            Directory.Delete(_updates, recursive: true);
        }
    }
}

/// <summary>
/// An application with the chat on and a runner that is a stand-in in the process (ADR 0035): it records what Mnemarr sends it and answers what a real one answers. The MCP write tools are on,
/// as the chat needs them to change anything.
/// </summary>
public sealed class ChatFactory : MnemarrFactoryBase
{
    public ChatFactory()
        : base(createFolder: true)
    {
    }

    /// <summary>The stand-in for the runner.</summary>
    public FakeRunner Runner { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.PostConfigure<Mnemarr.Core.Chat.AgentOptions>(options =>
        {
            options.Url = "http://runner.example:8489";
            options.Secret = new string('s', 40);
        });
        services.PostConfigure<McpOptions>(options =>
        {
            options.WriteTools = true;
            options.SourceTools = true;
        });
        services.AddHttpClient<Mnemarr.Api.V1.Chat.AgentRunnerClient>().ConfigurePrimaryHttpMessageHandler(() => Runner);
    }
}

/// <summary>A job of the stand-in runner (the consolidation): whether it is busy, and the events of what the model did, as the real runner tells them.</summary>
public sealed class FakeJob
{
    public volatile bool Busy;

    public List<System.Text.Json.Nodes.JsonNode> Events { get; } = [];

    /// <summary>The model called a tool of Mnemarr, and it answered (an error or not).</summary>
    public void Called(string id, string tool, bool isError)
    {
        lock (Events)
        {
            Events.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "tool-call", ["id"] = id, ["name"] = "mcp__mnemarr__" + tool, ["input"] = new System.Text.Json.Nodes.JsonObject() });
            Events.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "tool-result", ["id"] = id, ["isError"] = isError, ["summary"] = string.Empty });
        }
    }

    public void Ended(bool isError, string? message = null)
    {
        lock (Events)
        {
            if (message is not null)
            {
                Events.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "error", ["message"] = message });
            }

            Events.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "turn-end", ["isError"] = isError });
        }

        Busy = false;
    }

    public System.Text.Json.Nodes.JsonArray Snapshot()
    {
        lock (Events)
        {
            return new System.Text.Json.Nodes.JsonArray([.. Events.Select(node => node.DeepClone())]);
        }
    }
}

/// <summary>
/// An application with the consolidation on (ADR 0036) and a runner that is a stand-in in the process: a job is given to a script that calls the MCP server the way a model would. The memory
/// vault and the conversations are made by the tests.
/// </summary>
public sealed class ConsolidationFactory : MnemarrFactoryBase
{
    public ConsolidationFactory()
        : base(createFolder: true)
    {
    }

    public FakeRunner Runner { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Consolidation:Enabled", "true");
        builder.UseSetting("Consolidation:PassSeconds", "86400");
        builder.UseSetting("Consolidation:PollSeconds", "1");
        builder.UseSetting("Consolidation:MaxTries", "1");
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.PostConfigure<Mnemarr.Core.Chat.AgentOptions>(options =>
        {
            options.Url = "http://runner.example:8489";
            options.Secret = new string('s', 40);
        });
        services.AddHttpClient<Mnemarr.Api.V1.Chat.AgentRunnerClient>().ConfigurePrimaryHttpMessageHandler(() => Runner);
    }
}

/// <summary>A runner that is not one: it keeps every request and answers the conversations it has made.</summary>
public sealed class FakeRunner : HttpMessageHandler
{
    private readonly Dictionary<string, FakeJob> _jobs = [];
    private readonly List<string> _jobPrompts = [];

    /// <summary>What the stand-in does when a job is given its message: it gets the token of the run, and the prompt; it may call the MCP server as a model would, and say how it went on the job.</summary>
    public Func<string, string, FakeJob, Task>? JobBehaviour { get; set; }

    /// <summary>The prompts the jobs were given.</summary>
    public IReadOnlyList<string> JobPrompts
    {
        get
        {
            lock (_jobPrompts)
            {
                return [.. _jobPrompts];
            }
        }
    }

    /// <summary>How many jobs the runner still holds (a job is removed when it is done).</summary>
    public int JobsHeld
    {
        get
        {
            lock (_jobs)
            {
                return _jobs.Count;
            }
        }
    }

    private readonly List<(HttpMethod Method, string Path, string? Body, string? Authorization)> _requests = [];
    private readonly HashSet<string> _conversations = [];
    private readonly HashSet<string> _headerNames = [];

    /// <summary>The names of the headers that reached the runner, from every request.</summary>
    public IReadOnlySet<string> HeaderNames
    {
        get
        {
            lock (_headerNames)
            {
                return new HashSet<string>(_headerNames, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>What the runner answers to the list of its agents (the default has one that can run).</summary>
    public string AgentsJson { get; set; } = """[{"id":"claude-code","name":"Claude Code","available":true,"defaultModel":"sonnet","models":[{"id":"sonnet","label":"Sonnet"}]}]""";

    /// <summary>When true, the runner never starts to answer the stream (it ends as a cancelled call, as a timeout does).</summary>
    public bool Stalls { get; set; }

    /// <summary>Makes the runner forget a conversation, as it does when its volume is lost or room is made for new ones.</summary>
    public void Forget(string id)
    {
        lock (_conversations)
        {
            _conversations.Remove(id);
        }
    }

    public IReadOnlyList<(HttpMethod Method, string Path, string? Body, string? Authorization)> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>When true, the runner does not answer.</summary>
    public bool Down { get; set; }

    /// <summary>The status the runner answers to every call, when it refuses (the shared secret is wrong, for example).</summary>
    public HttpStatusCode? Refuses { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests)
        {
            _requests.Add((request.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
        }

        lock (_headerNames)
        {
            foreach (var header in request.Headers)
            {
                _headerNames.Add(header.Key);
            }

            foreach (var header in request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            {
                _headerNames.Add(header.Key);
            }
        }

        if (Down)
        {
            throw new HttpRequestException("The runner is down.");
        }

        if (Refuses is { } refusal)
        {
            return new HttpResponseMessage(refusal);
        }

        var path = request.RequestUri!.AbsolutePath;
        if (HandleJob(request, path, body) is { } jobAnswer)
        {
            return jobAnswer;
        }

        if (path == "/v1/agents")
        {
            return Json(HttpStatusCode.OK, AgentsJson);
        }

        if (request.Method == HttpMethod.Post && path == "/v1/conversations")
        {
            var id = Guid.NewGuid().ToString();
            lock (_conversations)
            {
                _conversations.Add(id);
            }

            return Json(HttpStatusCode.Created, $$"""{"id":"{{id}}","title":"","agent":"claude-code","model":"sonnet","createdAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-08T00:00:00Z","running":false,"busy":false,"pendingPermissions":0}""");
        }

        if (request.Method == HttpMethod.Get && path == "/v1/conversations")
        {
            string[] ids;
            lock (_conversations)
            {
                ids = [.. _conversations];
            }

            return Json(HttpStatusCode.OK, "[" + string.Join(",", ids.Select(id => $$"""{"id":"{{id}}","title":"t","agent":"claude-code","model":"sonnet","createdAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-08T00:00:00Z","running":false,"busy":false,"pendingPermissions":0}""")) + "]");
        }

        if (request.Method == HttpMethod.Delete)
        {
            lock (_conversations)
            {
                _conversations.Remove(path[(path.LastIndexOf('/') + 1)..]);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/events", StringComparison.Ordinal))
        {
            if (Stalls)
            {
                throw new TaskCanceledException("The runner did not answer in time.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("id: 1\ndata: {\"type\":\"user\",\"text\":\"hi\",\"seq\":1}\n\n", System.Text.Encoding.UTF8, "text/event-stream") };
        }

        return Json(HttpStatusCode.Accepted, "{}");
    }

    private HttpResponseMessage? HandleJob(HttpRequestMessage request, string path, string? body)
    {
        const string collection = "/v1/conversations";
        if (request.Method == HttpMethod.Post && path == collection && body is not null && System.Text.Json.Nodes.JsonNode.Parse(body)?["policy"]?.GetValue<string>() == "job")
        {
            var id = Guid.NewGuid().ToString();
            lock (_jobs)
            {
                _jobs[id] = new FakeJob();
            }

            return Json(HttpStatusCode.Created, $$"""{"id":"{{id}}","title":"consolidation","agent":"claude-code","model":"haiku","policy":"job","createdAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-08T00:00:00Z","running":false,"busy":false,"pendingPermissions":0}""");
        }

        if (!path.StartsWith(collection + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = path[(collection.Length + 1)..].Split('/');
        FakeJob? job;
        lock (_jobs)
        {
            _jobs.TryGetValue(rest[0], out job);
        }

        if (job is null)
        {
            return null;
        }

        if (request.Method == HttpMethod.Delete && rest.Length == 1)
        {
            lock (_jobs)
            {
                _jobs.Remove(rest[0]);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Get && rest.Length == 1)
        {
            var state = new System.Text.Json.Nodes.JsonObject { ["id"] = rest[0], ["busy"] = job.Busy, ["events"] = job.Snapshot() };
            return Json(HttpStatusCode.OK, state.ToJsonString());
        }

        if (request.Method == HttpMethod.Post && rest is [_, "messages"] && body is not null)
        {
            var message = System.Text.Json.Nodes.JsonNode.Parse(body)!;
            var token = message["mcpToken"]!.GetValue<string>();
            var prompt = message["text"]!.GetValue<string>();
            lock (_jobPrompts)
            {
                _jobPrompts.Add(prompt);
            }

            job.Busy = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (JobBehaviour is { } behaviour)
                    {
                        await behaviour(token, prompt, job);
                    }
                }
                catch (Exception exception)
                {
                    job.Ended(true, exception.GetType().Name);
                    return;
                }

                job.Ended(false);
            });
            return Json(HttpStatusCode.Accepted, "{}");
        }

        return null;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
