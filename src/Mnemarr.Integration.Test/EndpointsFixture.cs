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
        "api/v1/chat",
        "api/v1/chat/conversations",
        "api/v1/chat/conversations",
        "api/v1/chat/conversations/{id}",
        "api/v1/chat/conversations/{id}",
        "api/v1/chat/conversations/{id}/cancel",
        "api/v1/chat/conversations/{id}/events",
        "api/v1/chat/conversations/{id}/messages",
        "api/v1/chat/conversations/{id}/permissions/{requestId}",
        "api/v1/collectors",
        "api/v1/collectors/{machine}",
        "api/v1/collectors/{machine}/consolidate",
        "api/v1/collectors/{machine}/erase",
        "api/v1/collectors/{machine}/forget",
        "api/v1/collectors/{machine}/merge",
        "api/v1/collectors/{machine}/remove",
        "api/v1/collectors/{machine}/rename",
        "api/v1/config",
        "api/v1/config",
        "api/v1/config/status",
        "api/v1/consolidation",
        "api/v1/consolidation/retry",
        "api/v1/consolidation/run",
        "api/v1/context",
        "api/v1/conversations/title",
        "api/v1/conversations/title",
        "api/v1/conversations/title/rewrite",
        "api/v1/conversations/titles",
        "api/v1/conversations/titles/rewrite",
        "api/v1/enrolments",
        "api/v1/enrolments",
        "api/v1/enrolments/requests",
        "api/v1/enrolments/{id:guid}/approve",
        "api/v1/enrolments/{id:guid}/reject",
        "api/v1/identity",
        "api/v1/identity",
        "api/v1/identity/logo",
        "api/v1/identity/logo",
        "api/v1/identity/logo",
        "api/v1/ingest/conversations",
        "api/v1/ingest/download/{platform}",
        "api/v1/ingest/enrol",
        "api/v1/ingest/heartbeat",
        "api/v1/ingest/install.ps1",
        "api/v1/ingest/install.sh",
        "api/v1/ingest/installer/{platform}",
        "api/v1/ingest/pair",
        "api/v1/ingest/pair/poll",
        "api/v1/ingest/positions",
        @"api/v1/ingest/updates/{name:regex(^releases\.[A-Za-z0-9._-]+\.json$)}",
        "api/v1/ingest/updates/{name}",
        "api/v1/notes",
        "api/v1/notes/tags",
        "api/v1/notes/{sourceId:guid}/append",
        "api/v1/notes/{sourceId:guid}/preview",
        "api/v1/notes/{sourceId:guid}/rename",
        "api/v1/notes/{sourceId:guid}/{**path}",
        "api/v1/notes/{sourceId:guid}/{**path}",
        "api/v1/notes/{sourceId:guid}/{**path}",
        "api/v1/notes/{sourceId:guid}/{**path}",
        "api/v1/notifications",
        "api/v1/notifications/test",
        "api/v1/pairings",
        "api/v1/pairings/lookup",
        "api/v1/pairings/{id:guid}/approve",
        "api/v1/pairings/{id:guid}/reject",
        "api/v1/roles",
        "api/v1/search",
        "api/v1/search/status",
        "api/v1/sources",
        "api/v1/sources",
        "api/v1/sources/kinds",
        "api/v1/sources/roots",
        "api/v1/sources/{id:guid}",
        "api/v1/sources/{id:guid}",
        "api/v1/sources/{id:guid}",
        "api/v1/sources/{id:guid}/index",
        "api/v1/sources/{id:guid}/sync",
        "api/v1/sources/{sourceId:guid}/files/{**path}",
            "api/v1/system/status",
            "mcp",
            "mcp",
            "{*path:nonfile}");
    }

    /// <summary>The short list of what anyone may call without signing in (section 6 of the specification).</summary>
    [Fact]
    public void should_let_only_the_probes_the_shell_the_sign_in_and_what_a_machine_fetches_to_join_be_anonymous()
    {
        var anonymous = Endpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal);

        anonymous.Should().Equal("/health/live", "/health/ready", "api/v1/auth/login", "api/v1/ingest/download/{platform}", "api/v1/ingest/enrol", "api/v1/ingest/install.ps1", "api/v1/ingest/install.sh", "api/v1/ingest/installer/{platform}", "api/v1/ingest/pair", "api/v1/ingest/pair/poll", @"api/v1/ingest/updates/{name:regex(^releases\.[A-Za-z0-9._-]+\.json$)}", "api/v1/ingest/updates/{name}", "{*path:nonfile}");
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
        // The MCP endpoint is for whoever is signed in; what each tool needs is checked by the tool (and a tool that is not allowed is not even listed).
        withoutPermission.Should().Equal("/api/{**path}", "/openapi/{documentName}.json", "api/v1/auth/logout", "api/v1/auth/password", "api/v1/auth/session", "mcp", "mcp");
    }
}
