using System.Runtime.InteropServices;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Core.Test.SystemInfo;

public sealed class SystemStatusServiceFixture
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void should_report_the_time_the_service_started()
    {
        var subject = new SystemStatusService(new FakeTimeProvider(Start));

        subject.GetStatus().StartedAt.Should().Be(Start);
    }

    [Fact]
    public void should_report_no_uptime_when_just_started()
    {
        var subject = new SystemStatusService(new FakeTimeProvider(Start));

        subject.GetStatus().Uptime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void should_report_the_elapsed_time_as_uptime_when_the_clock_advances()
    {
        var clock = new FakeTimeProvider(Start);
        var subject = new SystemStatusService(clock);

        clock.Advance(TimeSpan.FromMinutes(90));

        subject.GetStatus().Uptime.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void should_report_the_build_version_and_the_runtime()
    {
        var status = new SystemStatusService(new FakeTimeProvider(Start)).GetStatus();

        status.Version.Should().Be(BuildInfo.Version);
        status.RuntimeVersion.Should().Be(RuntimeInformation.FrameworkDescription);
    }
}
