using System.Net;
using System.Net.Http.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The forgetting of old requests, on a server of its own: it moves the clock by days, which ends the sessions that other tests share.</summary>
public sealed class PairingCleanupApiFixture : IClassFixture<EnrolledOnlyFactory>
{
    private const string Pair = "/api/v1/ingest/pair";
    private readonly EnrolledOnlyFactory _factory;

    public PairingCleanupApiFixture(EnrolledOnlyFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private void NewMinute() => _factory.Clock.Advance(TimeSpan.FromSeconds(61));

    [Fact]
    public async Task should_forget_a_request_a_day_after_its_time_ended_and_tell_a_machine_that_asks_then_that_it_is_not_valid()
    {
        using var anonymous = _factory.CreateBrowserClient();
        NewMinute();
        var started = await (await anonymous.PostAsJsonAsync(Pair, new { hostname = "asleep" }, Token)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Token);
        var deviceCode = started.GetProperty("deviceCode").GetString();
        _factory.Clock.Advance(TimeSpan.FromMinutes(11));
        var told = await anonymous.PostAsJsonAsync("/api/v1/ingest/pair/poll", new { deviceCode }, Token);
        (await told.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Token)).GetProperty("state").GetString().Should().Be("expired", "the machine is told for a day");

        _factory.Clock.Advance(TimeSpan.FromDays(2));
        NewMinute();
        (await anonymous.PostAsJsonAsync(Pair, new { hostname = "another" }, Token)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await anonymous.PostAsJsonAsync("/api/v1/ingest/pair/poll", new { deviceCode }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
