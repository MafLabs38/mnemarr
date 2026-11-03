using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>The application's clock, so that a test can let time pass without waiting.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DataFolder", DataFolder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            // Applied after every source, the MNEMARR__ variables of the machine included.
            services.PostConfigure<MnemarrOptions>(options => options.DataFolder = DataFolder);
            ConfigureServices(services);
        });
    }

    /// <summary>Replaces services of the application for one kind of test.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
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
