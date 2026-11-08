using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

/// <summary>A role of Mnemarr itself (the consolidation) is given by nobody: not when an account is made, not when one is changed, and the account of Mnemarr can still be disabled.</summary>
public sealed class SystemRoleFixture : IDisposable
{
    private readonly AccountsHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void should_refuse_the_role_of_mnemarr_to_an_account_made_by_a_person()
    {
        var act = () => _h.Service.Create("robot", AccountKind.Service, Roles.Consolidator, null, "test");

        act.Should().Throw<RejectedException>().WithMessage("*belongs to Mnemarr itself*");
    }

    [Fact]
    public void should_refuse_to_change_an_account_into_the_role_of_mnemarr()
    {
        var robot = _h.Robot("robot", Roles.Assistant);

        var act = () => _h.Service.Update(robot.Id, Roles.Consolidator, enabled: true, "test");

        act.Should().Throw<RejectedException>();
    }

    [Fact]
    public void should_let_mnemarr_make_its_own_account_and_a_person_disable_it_but_not_change_its_role()
    {
        var own = _h.Service.CreateSystem("consolidation", Roles.Consolidator, "mnemarr");

        var disabled = _h.Service.Update(own.Id, Roles.Consolidator, enabled: false, "test");
        var act = () => _h.Service.Update(own.Id, Roles.Reader, enabled: true, "test");

        disabled.Enabled.Should().BeFalse();
        act.Should().NotThrow("a service account may be given an ordinary role again");
    }

    [Fact]
    public void should_not_list_the_role_as_one_to_give()
    {
        Roles.All.Where(role => role.System).Select(role => role.Name).Should().Equal(Roles.Consolidator);
        Roles.Find(Roles.Consolidator)!.Permissions.Should().NotContain(Permissions.ConversationsRead);
    }
}
