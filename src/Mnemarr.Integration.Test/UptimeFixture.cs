using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

[Trait("Category", "Integration")]
public sealed class UptimeFixture
{
    [Fact]
    public async Task should_measure_uptime_from_the_start_of_the_application_not_from_the_first_request()
    {
        await using var factory = new MnemarrFactory();
        using var client = factory.CreateClient();

        factory.Clock.Advance(TimeSpan.FromMinutes(90));
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        status.GetProperty("uptimeSeconds").GetInt64().Should().Be(90 * 60);
    }
}
