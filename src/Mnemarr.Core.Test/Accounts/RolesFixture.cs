using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class RolesFixture
{
    [Fact]
    public void should_give_the_administrator_every_permission()
    {
        Roles.Find(Roles.Admin)!.Permissions.Should().BeEquivalentTo(Permissions.All);
    }

    [Theory]
    [InlineData(Roles.Reader, new[] { Permissions.NotesRead, Permissions.Search })]
    [InlineData(Roles.Collector, new[] { Permissions.IngestWrite })]
    [InlineData(Roles.Monitor, new[] { Permissions.SystemRead })]
    [InlineData(Roles.Assistant, new[] { Permissions.NotesRead, Permissions.NotesWrite, Permissions.Search, Permissions.ConversationsRead, Permissions.CollectorsRequest })]
    [InlineData(Roles.Operator, new[] { Permissions.NotesRead, Permissions.NotesWrite, Permissions.Search, Permissions.SourcesManage, Permissions.SystemRead })]
    public void should_give_a_role_exactly_its_permissions_and_nothing_else(string role, string[] expected)
    {
        Roles.Find(role)!.Permissions.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void should_know_no_role_by_another_case_and_give_nothing_to_an_unknown_role()
    {
        Roles.Find("admin").Should().BeNull();
        new Account(Guid.NewGuid(), "x", AccountKind.User, "Gone", true, DateTimeOffset.UnixEpoch).Permissions.Should().BeEmpty();
    }

    [Fact]
    public void should_let_a_key_reach_only_the_sources_it_is_limited_to()
    {
        var allowed = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), "x", AccountKind.Service, Roles.Reader, true, DateTimeOffset.UnixEpoch);

        var limited = new AccessPrincipal(account, account.Permissions, [allowed], null);
        var unlimited = new AccessPrincipal(account, account.Permissions, null, null);

        limited.Scope.CanAccess(allowed).Should().BeTrue();
        limited.Scope.CanAccess(Guid.NewGuid()).Should().BeFalse();
        unlimited.Scope.CanAccess(Guid.NewGuid()).Should().BeTrue();
        limited.Can(Permissions.NotesRead).Should().BeTrue();
        limited.Can(Permissions.AccountsManage).Should().BeFalse();
    }
}
