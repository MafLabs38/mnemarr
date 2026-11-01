using System.Runtime.Versioning;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Health;

namespace Mnemarr.Core.Test.Health;

public sealed class DataFolderHealthCheckFixture : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mnemarr-health-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Task<HealthCheckResult> Check(string folder) =>
        new DataFolderHealthCheck(Options.Create(new MnemarrOptions { DataFolder = folder }))
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task should_be_healthy_when_the_folder_is_writable()
    {
        var result = await Check(_folder);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_leave_no_probe_file_behind()
    {
        await Check(_folder);

        Directory.GetFileSystemEntries(_folder).Should().BeEmpty();
    }

    [Fact]
    public async Task should_be_unhealthy_when_the_folder_does_not_exist()
    {
        var result = await Check(Path.Combine(_folder, "missing"));

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task should_not_reveal_the_folder_path_when_unhealthy()
    {
        var missing = Path.Combine(_folder, "missing");

        var result = await Check(missing);

        result.Description.Should().NotContain(missing);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task should_be_unhealthy_when_the_folder_is_not_writable()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess,
            "Needs a Unix permission model and a user who is not privileged: root can write anywhere.");
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var result = await Check(_folder);

            result.Status.Should().Be(HealthStatus.Unhealthy);
            result.Description.Should().Be("The data folder is not writable.");
        }
        finally
        {
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
