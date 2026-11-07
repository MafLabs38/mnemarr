using System.Net;
using System.Net.Http.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The limits on the requests to be linked, on a server of their own: the requests that other tests leave waiting would count here.</summary>
public sealed class PairingLimitsApiFixture : IClassFixture<EnrolledOnlyFactory>
{
    private const string Pair = "/api/v1/ingest/pair";
    private readonly EnrolledOnlyFactory _factory;

    public PairingLimitsApiFixture(EnrolledOnlyFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private void NewMinute() => _factory.Clock.Advance(TimeSpan.FromSeconds(61));

    [Fact]
    public async Task should_limit_how_many_requests_may_be_made_in_a_minute_and_how_many_may_wait()
    {
        NewMinute();
        using var anonymous = _factory.CreateBrowserClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await anonymous.PostAsJsonAsync(Pair, new { hostname = $"pc-{i}" }, Token)).StatusCode);
        }

        statuses.Count(status => status == HttpStatusCode.Created).Should().Be(10);
        statuses.Last().Should().Be(HttpStatusCode.TooManyRequests);

        // Two minutes later ten more come, and ten were already waiting: the twenty-first has to wait for a person.
        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 10; i++)
        {
            (await anonymous.PostAsJsonAsync(Pair, new { hostname = $"pc-{i}" }, Token)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        (await anonymous.PostAsJsonAsync(Pair, new { hostname = "one-too-many" }, Token)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "twenty requests wait for a person");
    }
}
