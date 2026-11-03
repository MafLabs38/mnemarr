using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Mnemarr.Integration.Test;

public sealed class SecureCookieFixture : IClassFixture<SecureCookieFactory>
{
    private readonly SecureCookieFactory _factory;

    public SecureCookieFixture(SecureCookieFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task should_make_the_session_cookie_secure_unless_the_operator_chose_otherwise()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Mnemarr-Request", "1");

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = MnemarrFactory.AdminName, password = MnemarrFactory.AdminPassword }, TestContext.Current.CancellationToken);

        login.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Should().Contain("secure").And.Contain("httponly").And.Contain("samesite=strict");
    }
}

public sealed class NoAdministratorFixture : IClassFixture<NoAdministratorFactory>
{
    private readonly NoAdministratorFactory _factory;

    public NoAdministratorFixture(NoAdministratorFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task should_say_in_the_readiness_probe_that_nobody_can_sign_in_without_failing_it()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("Degraded");
    }

    [Fact]
    public async Task should_let_nobody_in()
    {
        using var client = _factory.CreateBrowserClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = "admin", password = MnemarrFactory.AdminPassword }, TestContext.Current.CancellationToken);

        login.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
