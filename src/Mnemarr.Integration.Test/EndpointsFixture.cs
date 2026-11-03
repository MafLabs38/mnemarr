using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Mnemarr.Integration.Test;

[Collection("Host")]
public sealed class EndpointsFixture
{
    private readonly MnemarrFactory _factory;

    public EndpointsFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Every route the host answers, and they are all anonymous today (docs/SPEC.md, section 6: only the probes and the shell,
    /// plus the status and the OpenAPI document until authentication lands). A new route is added to this list on purpose, with
    /// the question "may it be anonymous?" asked. When authentication arrives, the anonymous routes must be exactly the ones the
    /// specification names, and this test is where that is checked.
    /// </summary>
    [Fact]
    public void should_expose_exactly_these_routes_and_nothing_else_by_accident()
    {
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal);

        // Controller routes are registered without the leading slash, the minimal ones with it.
        routes.Should().Equal(
            "/api/{**path}",
            "/health/live",
            "/health/ready",
            "/openapi/{documentName}.json",
            "api/v1/system/status",
            "{*path:nonfile}");
    }
}
