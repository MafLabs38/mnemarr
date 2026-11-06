using Microsoft.AspNetCore.Identity;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AccountServiceFixture : IDisposable
{
    private const string Password = AccountsHarness.Password;
    private readonly AccountsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private AccountService Subject => _h.Service;

    [Fact]
    public void should_create_a_user_with_a_hashed_password_and_audit_it()
    {
        var account = Subject.Create("  alice  ", AccountKind.User, Roles.Reader, Password, "root");

        account.Should().Match<Account>(a => a.Name == "alice" && a.Kind == AccountKind.User && a.Role == Roles.Reader && a.Enabled && a.CreatedAt == _h.Clock.GetUtcNow());
        _h.Accounts.GetCredentials("alice")!.PasswordHash.Should().NotBeNullOrEmpty().And.NotContain(Password);
        _h.Audit.Recent(10).Should().ContainSingle().Which.Should().Match<AuditEvent>(e => e.Actor == "root" && e.Action == "account.created" && e.Target == "alice");
    }

    [Fact]
    public void should_create_a_service_account_without_a_password()
    {
        var account = Subject.Create("collector-laptop", AccountKind.Service, Roles.Collector, null, "root");

        _h.Accounts.GetCredentials("collector-laptop")!.PasswordHash.Should().BeNull();
        account.Kind.Should().Be(AccountKind.Service);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("")]
    public void should_refuse_a_name_that_is_not_a_plain_identifier(string name)
    {
        var create = () => Subject.Create(name, AccountKind.User, Roles.Reader, Password, "root");

        create.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
    }

    [Fact]
    public void should_refuse_a_name_one_character_too_long()
    {
        var create = () => Subject.Create(new string('a', AccountNames.MaxLength + 1), AccountKind.User, Roles.Reader, Password, "root");

        create.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(AccountNames.MaxLength)]
    public void should_accept_a_name_at_the_limits(int length)
    {
        var name = "a" + new string('b', length - 1);

        Subject.Create(name, AccountKind.User, Roles.Reader, Password, "root").Name.Should().Be(name);
    }

    [Theory]
    [InlineData(AccountKind.User, Roles.Assistant)]
    [InlineData(AccountKind.User, Roles.Collector)]
    [InlineData(AccountKind.Service, Roles.Admin)]
    [InlineData(AccountKind.User, "Nobody")]
    public void should_refuse_a_role_that_does_not_exist_or_is_not_for_that_kind_of_account(AccountKind kind, string role)
    {
        var create = () => Subject.Create("someone", kind, role, kind == AccountKind.User ? Password : null, "root");

        create.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
    }

    [Theory]
    [InlineData(AccountKind.User, Roles.Assistant)]
    [InlineData(AccountKind.Service, Roles.Admin)]
    [InlineData(AccountKind.User, "Nobody")]
    public void should_refuse_to_give_an_existing_account_a_role_that_does_not_fit_it_and_change_nothing(AccountKind kind, string role)
    {
        _h.Admin();
        var account = kind == AccountKind.User ? _h.Reader() : _h.Robot();

        var change = () => Subject.Update(account.Id, role, enabled: true, "root");

        change.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
        _h.Accounts.Get(account.Id)!.Role.Should().Be(account.Role);
    }

    [Fact]
    public void should_refuse_to_update_an_account_that_does_not_exist()
    {
        var change = () => Subject.Update(Guid.NewGuid(), Roles.Reader, enabled: true, "root");

        change.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("SYSTEM")]
    [InlineData("unknown")]
    public void should_refuse_a_name_that_the_audit_log_gives_to_what_is_not_an_account(string name)
    {
        var create = () => Subject.Create(name, AccountKind.Service, Roles.Reader, null, "root");

        create.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
    }

    [Theory]
    [InlineData(null, "needs a password")]
    [InlineData("short", "at least 12")]
    public void should_refuse_a_user_without_a_good_password(string? password, string reason)
    {
        var create = () => Subject.Create("alice", AccountKind.User, Roles.Reader, password, "root");

        create.Should().Throw<RejectedException>().Which.Should().Match<RejectedException>(e => e.Problem == Problem.Invalid && e.Message.Contains(reason));
    }

    [Fact]
    public void should_refuse_a_service_account_that_is_given_a_password()
    {
        var create = () => Subject.Create("svc", AccountKind.Service, Roles.Reader, Password, "root");

        create.Should().Throw<RejectedException>().WithMessage("*no password*");
    }

    [Fact]
    public void should_refuse_a_name_that_is_taken_whatever_its_case()
    {
        _h.Admin("Alice");

        var create = () => Subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");

        create.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Conflict);
    }

    [Fact]
    public void should_end_the_sessions_of_an_account_when_it_is_disabled_so_that_enabling_it_again_does_not_bring_a_copied_cookie_back()
    {
        _h.Admin();
        var alice = _h.Reader();
        var session = _h.Sessions.Start(alice.Id);

        Subject.SetEnabled(alice.Id, false, "root");
        Subject.SetEnabled(alice.Id, true, "root");

        _h.Sessions.AccountOf(session).Should().BeNull();
    }

    [Fact]
    public void should_change_a_role_and_audit_it()
    {
        _h.Admin();
        var alice = _h.Reader();

        Subject.ChangeRole(alice.Id, Roles.Operator, "root").Role.Should().Be(Roles.Operator);

        _h.Accounts.Get(alice.Id)!.Role.Should().Be(Roles.Operator);
        _h.Audit.Recent(1).Single().Should().Match<AuditEvent>(e => e.Action == "account.role-changed" && e.Detail == "Reader to Operator");
    }

    [Fact]
    public void should_change_the_role_and_the_state_in_one_step_and_audit_each_change_once()
    {
        _h.Admin();
        var alice = _h.Reader();

        var updated = Subject.Update(alice.Id, Roles.Operator, enabled: false, "root");

        updated.Should().Match<Account>(a => a.Role == Roles.Operator && !a.Enabled);
        _h.Audit.Recent(10).Select(e => e.Action).Should().Contain(["account.role-changed", "account.disabled"]);
        _h.Audit.Recent(10, "account.enabled").Should().BeEmpty();
    }

    [Fact]
    public void should_write_nothing_when_one_of_the_two_changes_is_refused()
    {
        var root = _h.Admin();

        var update = () => Subject.Update(root.Id, Roles.Admin, enabled: false, "root");

        update.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Conflict);
        _h.Accounts.Get(root.Id).Should().Match<Account>(a => a.Enabled && a.Role == Roles.Admin);
    }

    [Theory]
    [InlineData("demote")]
    [InlineData("disable")]
    [InlineData("delete")]
    public void should_refuse_to_demote_disable_or_delete_the_last_enabled_administrator(string what)
    {
        var root = _h.Admin();

        Action change = what switch
        {
            "demote" => () => Subject.ChangeRole(root.Id, Roles.Reader, "x"),
            "disable" => () => Subject.SetEnabled(root.Id, false, "x"),
            _ => () => Subject.Delete(root.Id, "x"),
        };

        change.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Conflict);
        _h.Accounts.CountEnabledAdmins().Should().Be(1);
    }

    [Fact]
    public void should_allow_it_once_another_administrator_is_enabled_and_count_only_enabled_ones()
    {
        var root = _h.Admin();
        var second = _h.Admin("second");

        Subject.SetEnabled(second.Id, false, "x");
        new Action(() => Subject.Delete(root.Id, "x")).Should().Throw<RejectedException>();
        Subject.SetEnabled(second.Id, true, "x");

        Subject.Delete(root.Id, "x");
        _h.Accounts.CountEnabledAdmins().Should().Be(1);
        _h.Audit.Recent(10, "account.deleted").Should().ContainSingle().Which.Target.Should().Be("root");
    }

    [Fact]
    public void should_keep_an_administrator_an_administrator_when_the_role_is_set_to_admin_again()
    {
        var root = _h.Admin();

        Subject.ChangeRole(root.Id, Roles.Admin, "x").Role.Should().Be(Roles.Admin);
    }

    [Fact]
    public void should_end_the_sessions_of_an_account_when_its_password_changes_except_the_one_kept()
    {
        var alice = _h.Reader();
        var kept = _h.Sessions.Start(alice.Id);
        var other = _h.Sessions.Start(alice.Id);

        Subject.SetPassword(alice.Id, "another long password", "alice", keepSession: kept);

        _h.Sessions.AccountOf(kept).Should().Be(alice.Id);
        _h.Sessions.AccountOf(other).Should().BeNull();
    }

    [Fact]
    public void should_end_every_session_when_an_administrator_sets_the_password()
    {
        var alice = _h.Reader();
        var session = _h.Sessions.Start(alice.Id);

        Subject.SetPassword(alice.Id, "another long password", "root");

        _h.Sessions.AccountOf(session).Should().BeNull();
    }

    [Fact]
    public void should_refuse_a_password_for_a_service_account()
    {
        var svc = _h.Robot();

        new Action(() => Subject.SetPassword(svc.Id, Password, "x")).Should().Throw<RejectedException>().WithMessage("*no password*");
    }

    [Theory]
    [InlineData("role")]
    [InlineData("enabled")]
    [InlineData("password")]
    [InlineData("delete")]
    public void should_say_there_is_no_such_account_for_an_unknown_id(string operation)
    {
        var id = Guid.NewGuid();

        Action action = operation switch
        {
            "role" => () => Subject.ChangeRole(id, Roles.Reader, "x"),
            "enabled" => () => Subject.SetEnabled(id, true, "x"),
            "password" => () => Subject.SetPassword(id, Password, "x"),
            _ => () => Subject.Delete(id, "x"),
        };

        action.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public void should_enable_an_account_again_and_audit_both_changes()
    {
        _h.Admin();
        var alice = _h.Reader();

        Subject.SetEnabled(alice.Id, false, "root").Enabled.Should().BeFalse();
        Subject.SetEnabled(alice.Id, true, "root").Enabled.Should().BeTrue();

        _h.Audit.Recent(2).Select(e => e.Action).Should().Equal("account.enabled", "account.disabled");
    }

    [Theory]
    [InlineData("nobody", "a brand new password", Problem.NotFound)]
    [InlineData("agent", "a brand new password", Problem.Invalid)]
    [InlineData("alice", "short", Problem.Invalid)]
    public void should_refuse_a_reset_of_nobody_of_a_service_or_to_a_weak_password(string name, string password, Problem problem)
    {
        _h.Reader();
        _h.Robot();

        new Action(() => Subject.Reset(name, password)).Should().Throw<RejectedException>().Which.Problem.Should().Be(problem);
    }
}
