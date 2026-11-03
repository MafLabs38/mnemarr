using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Core.Test;

public sealed class CoreServiceCollectionExtensionsFixture
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddMnemarrCore(configuration).BuildServiceProvider();
    }

    [Fact]
    public void should_default_the_data_folder_when_nothing_is_configured()
    {
        using var provider = Build([]);

        provider.GetRequiredService<IOptions<MnemarrOptions>>().Value.DataFolder.Should().Be("/config");
    }

    [Fact]
    public void should_keep_the_start_time_when_the_status_is_read_again_later()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock);
        services.AddMnemarrCore(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<ISystemStatusService>().GetStatus();
        clock.Advance(TimeSpan.FromHours(1));
        var second = provider.GetRequiredService<ISystemStatusService>().GetStatus();

        second.StartedAt.Should().Be(first.StartedAt);
        second.Uptime.Should().Be(TimeSpan.FromHours(1));
    }
}
