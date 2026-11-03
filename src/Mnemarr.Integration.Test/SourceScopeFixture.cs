using System.Security.Claims;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Integration.Test;

/// <summary>The one way to ask whether a caller may touch a source: it fails closed, so a handler cannot serve every source by forgetting to ask.</summary>
public sealed class SourceScopeFixture
{
    private static readonly Account Service = new(Guid.NewGuid(), "svc", AccountKind.Service, Roles.Assistant, Enabled: true, DateTimeOffset.UnixEpoch);

    private static ClaimsPrincipal Caller(IReadOnlyList<Guid>? sources) =>
        new(new ClaimsIdentity(MnemarrClaims.For(new AccessPrincipal(Service, Service.Permissions, sources, Guid.NewGuid())), "test"));

    [Fact]
    public void should_let_a_caller_who_is_not_signed_in_touch_nothing()
    {
        new ClaimsPrincipal(new ClaimsIdentity()).CanAccessSource(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void should_let_a_caller_that_is_not_limited_touch_any_source()
    {
        Caller(null).CanAccessSource(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void should_let_a_key_limited_to_sources_touch_those_and_no_other()
    {
        var mine = Guid.NewGuid();
        var caller = Caller([mine]);

        caller.CanAccessSource(mine).Should().BeTrue();
        caller.CanAccessSource(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void should_let_a_key_limited_to_no_source_touch_none_rather_than_all()
    {
        Caller([]).CanAccessSource(Guid.NewGuid()).Should().BeFalse();
    }
}
