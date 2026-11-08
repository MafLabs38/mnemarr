using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

/// <summary>An account of Mnemarr itself (the consolidation) is invisible to people: nobody gives its role, lists it, changes it, deletes it or makes a key for it.</summary>
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
    public void should_let_mnemarr_make_its_own_account_and_leave_it_to_nobody_else()
    {
        var own = _h.Service.CreateSystem("consolidation", Roles.Consolidator, "mnemarr");

        own.IsSystem.Should().BeTrue();
        var change = () => _h.Service.Update(own.Id, Roles.Reader, enabled: true, "test");
        var key = () => _h.ApiKeys.Create(own.Id, "a key", null, null, "test");
        change.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
        key.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public void should_not_list_the_role_as_one_to_give_and_give_the_job_no_conversation()
    {
        Roles.All.Where(role => role.System).Select(role => role.Name).Should().Equal(Roles.Consolidator);
        Roles.Find(Roles.Consolidator)!.Permissions.Should().NotContain(Permissions.ConversationsRead);
    }
}
