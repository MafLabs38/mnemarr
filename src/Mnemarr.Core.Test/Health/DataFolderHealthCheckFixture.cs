using System.Runtime.Versioning;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Health;

namespace Mnemarr.Core.Test.Health;

public sealed class DataFolderHealthCheckFixture : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mnemarr-health-").FullName;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private DataFolderHealthCheck Subject(string folder) =>
        new(Options.Create(new MnemarrOptions { DataFolder = folder }), _clock);

    private static Task<HealthCheckResult> Check(DataFolderHealthCheck subject) =>
        subject.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task should_be_healthy_and_leave_no_probe_file_when_the_folder_is_writable()
    {
        var result = await Check(Subject(_folder));

        result.Status.Should().Be(HealthStatus.Healthy);
        Directory.GetFileSystemEntries(_folder).Should().BeEmpty();
    }

    [Fact]
    public async Task should_be_unhealthy_without_revealing_the_path_when_the_folder_does_not_exist()
    {
        var missing = Path.Combine(_folder, "missing");

        var result = await Check(Subject(missing));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("The data folder does not exist.").And.NotContain(missing);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task should_be_unhealthy_and_keep_the_cause_when_the_folder_is_not_writable()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess,
            "Needs a Unix permission model and a user who is not privileged: root can write anywhere.");
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var result = await Check(Subject(_folder));

            result.Status.Should().Be(HealthStatus.Unhealthy);
            result.Description.Should().Be("The data folder is not writable.");
            result.Exception.Should().BeOfType<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task should_probe_once_when_many_requests_arrive_together_after_the_result_expired()
    {
        var subject = Subject(_folder);
        var probes = 0;
        using var watcher = new FileSystemWatcher(_folder, ".write-test-*") { EnableRaisingEvents = true };
        watcher.Created += (_, _) => Interlocked.Increment(ref probes);

        var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => Task.Run(() => Check(subject), TestContext.Current.CancellationToken)));
        await Task.Delay(300, TestContext.Current.CancellationToken); // file events reach the watcher a moment later

        results.Should().OnlyContain(result => result.Status == HealthStatus.Healthy);
        probes.Should().Be(1);
    }

    [Fact]
    public async Task should_reuse_its_result_for_a_few_seconds_so_that_a_loop_of_requests_does_not_write_on_every_one()
    {
        var folder = Path.Combine(_folder, "volume");
        Directory.CreateDirectory(folder);
        var subject = Subject(folder);
        (await Check(subject)).Status.Should().Be(HealthStatus.Healthy);

        Directory.Delete(folder);
        _clock.Advance(DataFolderHealthCheck.ReuseFor - TimeSpan.FromSeconds(1));

        (await Check(subject)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_see_a_volume_that_went_away_once_its_result_is_too_old()
    {
        var folder = Path.Combine(_folder, "volume");
        Directory.CreateDirectory(folder);
        var subject = Subject(folder);
        await Check(subject);

        Directory.Delete(folder);
        _clock.Advance(DataFolderHealthCheck.ReuseFor);

        (await Check(subject)).Status.Should().Be(HealthStatus.Unhealthy);
    }
}
