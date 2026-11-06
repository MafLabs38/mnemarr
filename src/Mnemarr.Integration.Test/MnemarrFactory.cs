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
    }
}
