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
    [InlineData(Roles.Assistant, new[] { Permissions.NotesRead, Permissions.NotesWrite, Permissions.Search })]
    [InlineData(Roles.Operator, new[] { Permissions.NotesRead, Permissions.NotesWrite, Permissions.Search, Permissions.SourcesManage, Permissions.SystemRead })]
    public void should_give_a_role_exactly_its_permissions_and_nothing_else(string role, string[] expected)
    {
        Roles.Find(role)!.Permissions.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void should_never_let_anyone_but_the_administrator_manage_accounts()
    {
        Roles.All.Where(role => role.Permissions.Contains(Permissions.AccountsManage)).Select(role => role.Name).Should().Equal(Roles.Admin);
    }

    [Fact]
    public void should_keep_the_ingest_permission_to_the_collector_and_the_administrator()
    {
        Roles.All.Where(role => role.Permissions.Contains(Permissions.IngestWrite)).Select(role => role.Name).Should().BeEquivalentTo(Roles.Admin, Roles.Collector);
    }

    [Fact]
    public void should_give_every_permission_to_at_least_one_role()
    {
        Permissions.All.Should().OnlyContain(permission => Roles.All.Any(role => role.Permissions.Contains(permission)));
    }

    [Fact]
    public void should_keep_administrator_for_people_only()
    {
        Roles.Find(Roles.Admin)!.Should().Match<Role>(role => role.ForUsers && !role.ForServices);
        Roles.Find(Roles.Assistant)!.Should().Match<Role>(role => !role.ForUsers && role.ForServices);
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

        limited.CanAccess(allowed).Should().BeTrue();
        limited.CanAccess(Guid.NewGuid()).Should().BeFalse();
        unlimited.CanAccess(Guid.NewGuid()).Should().BeTrue();
        limited.Can(Permissions.NotesRead).Should().BeTrue();
        limited.Can(Permissions.AccountsManage).Should().BeFalse();
    }
}
