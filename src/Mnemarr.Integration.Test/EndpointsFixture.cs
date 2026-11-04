using Microsoft.AspNetCore.Authorization;
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

    private IEnumerable<RouteEndpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    /// <summary>
    /// Every route the host answers. A new route is added to this list on purpose, with the question "who may call it?" asked: the
    /// permission it needs is declared on it, and one that declares nothing needs a sign-in (the fallback policy).
    /// </summary>
    [Fact]
    public void should_expose_exactly_these_routes_and_nothing_else_by_accident()
    {
        // Controller routes are registered without the leading slash, the minimal ones with it.
        Endpoints().Select(endpoint => endpoint.RoutePattern.RawText).Order(StringComparer.Ordinal).Should().Equal(
            "/api/{**path}",
            "/health/live",
            "/health/ready",
            "/openapi/{documentName}.json",
            "api/v1/accounts",
            "api/v1/accounts",
            "api/v1/accounts/{accountId:guid}/keys",
            "api/v1/accounts/{accountId:guid}/keys",
            "api/v1/accounts/{accountId:guid}/keys/{keyId:guid}",
            "api/v1/accounts/{id:guid}",
            "api/v1/accounts/{id:guid}",
            "api/v1/accounts/{id:guid}",
            "api/v1/accounts/{id:guid}/password",
            "api/v1/audit",
            "api/v1/auth/login",
            "api/v1/auth/logout",
            "api/v1/auth/password",
            "api/v1/auth/session",
        "api/v1/config",
        "api/v1/config",
            "api/v1/roles",
        "api/v1/search",
        "api/v1/sources",
        "api/v1/sources",
        "api/v1/sources/kinds",
        "api/v1/sources/roots",
        "api/v1/sources/{id:guid}",
        "api/v1/sources/{id:guid}",
        "api/v1/sources/{id:guid}",
        "api/v1/sources/{id:guid}/index",
            "api/v1/system/status",
            "{*path:nonfile}");
    }

    /// <summary>The short list of what anyone may call without signing in (section 6 of the specification).</summary>
    [Fact]
    public void should_let_only_the_probes_the_shell_and_the_sign_in_call_be_anonymous()
    {
        var anonymous = Endpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal);

        anonymous.Should().Equal("/health/live", "/health/ready", "api/v1/auth/login", "{*path:nonfile}");
    }

    [Fact]
    public void should_ask_a_permission_of_every_endpoint_that_is_not_for_everyone_who_is_signed_in()
    {
        var withoutPermission = Endpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().All(data => string.IsNullOrEmpty(data.Policy)))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal);

        // For any signed-in person: ending the session, who am I, changing one's own password, and what the fallback closes.
        withoutPermission.Should().Equal("/api/{**path}", "/openapi/{documentName}.json", "api/v1/auth/logout", "api/v1/auth/password", "api/v1/auth/session");
    }
}
