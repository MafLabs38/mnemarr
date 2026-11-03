using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Integration.Test;

/// <summary>The real application in memory, with its own data folder (created or not) that is removed afterwards.</summary>
public abstract class MnemarrFactoryBase : WebApplicationFactory<Program>
{
    private readonly bool _createFolder;

    protected MnemarrFactoryBase(bool createFolder)
    {
        _createFolder = createFolder;
        DataFolder = Path.Combine(Path.GetTempPath(), $"mnemarr-test-{Guid.NewGuid():N}");
        if (createFolder)
        {
            Directory.CreateDirectory(DataFolder);
        }
    }

    public string DataFolder { get; }

    /// <summary>The cookie of the test hosts is not <c>Secure</c> (they speak plain HTTP) unless a test asks for the default.</summary>
    protected virtual CookieSecurityMode CookieSecurity => CookieSecurityMode.SameAsRequest;

    /// <summary>Whether the host starts with a first administrator. A test of an empty Mnemarr turns it off.</summary>
    protected virtual bool HasAdministrator => true;

    /// <summary>The administrator every test host starts with, created from the settings like the first run of a real one.</summary>
    public const string AdminName = "admin";

    public const string AdminPassword = "integration-test-password";

    private readonly string _passwordFile = Path.Combine(Path.GetTempPath(), $"mnemarr-admin-{Guid.NewGuid():N}");

    /// <summary>The application's clock, so that a test can let time pass without waiting.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DataFolder", DataFolder);
        File.WriteAllText(_passwordFile, AdminPassword);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            // Applied after every source, the MNEMARR__ variables of the machine included.
            services.PostConfigure<MnemarrOptions>(options => options.DataFolder = DataFolder);
            services.PostConfigure<AuthOptions>(options =>
            {
                options.AdminUser = HasAdministrator ? AdminName : null;
                options.AdminPasswordFile = HasAdministrator ? _passwordFile : null;
                options.CookieSecurity = CookieSecurity;
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
