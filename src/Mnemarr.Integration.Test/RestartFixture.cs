using System.Net;
using System.Net.Http.Json;

namespace Mnemarr.Integration.Test;

/// <summary>What a restart of an instance does to the people signed in: sessions go on, and a break-glass reset ends them.</summary>
public sealed class RestartFixture
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task should_keep_a_session_across_a_restart_and_end_it_with_a_break_glass_reset()
    {
        await using var first = new MnemarrFactory();
        using var browser = first.CreateBrowserClient();
        var login = await browser.PostAsJsonAsync("/api/v1/auth/login", new { name = MnemarrFactory.AdminName, password = MnemarrFactory.AdminPassword }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        await using (var restarted = new RestartedFactory(first))
        {
            using var client = restarted.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK, "sessions survive a restart");
        }

        await using (var reset = new RestartedFactory(first, resetPassword: "a brand new password"))
        {
            using var client = reset.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a reset ends the sessions");
            using var fresh = reset.CreateBrowserClient();
            (await fresh.PostAsJsonAsync("/api/v1/auth/login", new { name = MnemarrFactory.AdminName, password = MnemarrFactory.AdminPassword }, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await fresh.PostAsJsonAsync("/api/v1/auth/login", new { name = MnemarrFactory.AdminName, password = "a brand new password" }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
