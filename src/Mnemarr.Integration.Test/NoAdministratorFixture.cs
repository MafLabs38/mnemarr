using System.Net.Http.Json;

namespace Mnemarr.Integration.Test;

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
