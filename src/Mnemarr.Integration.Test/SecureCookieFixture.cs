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
    public async Task should_make_the_session_cookie_secure_and_host_bound_unless_the_operator_chose_otherwise()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Mnemarr-Request", "1");

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = MnemarrFactory.AdminName, password = MnemarrFactory.AdminPassword }, TestContext.Current.CancellationToken);

        login.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Should().StartWith("__host-mnemarr.session=").And.Contain("secure").And.Contain("httponly").And.Contain("samesite=strict");
    }
}
