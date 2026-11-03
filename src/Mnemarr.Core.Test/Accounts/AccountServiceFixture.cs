using Microsoft.AspNetCore.Identity;
using Mnemarr.Core.Accounts;

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

        create.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
    }

    [Fact]
    public void should_refuse_a_name_one_character_too_long()
    {
        var create = () => Subject.Create(new string('a', AccountService.MaxNameLength + 1), AccountKind.User, Roles.Reader, Password, "root");

        create.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(AccountService.MaxNameLength)]
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

        create.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
    }

    [Theory]
    [InlineData(null, "needs a password")]
    [InlineData("short", "at least 12")]
    public void should_refuse_a_user_without_a_good_password(string? password, string reason)
    {
        var create = () => Subject.Create("alice", AccountKind.User, Roles.Reader, password, "root");

        create.Should().Throw<AccountRejectedException>().Which.Should().Match<AccountRejectedException>(e => e.Problem == AccountProblem.Invalid && e.Message.Contains(reason));
    }

    [Fact]
    public void should_refuse_a_service_account_that_is_given_a_password()
    {
        var create = () => Subject.Create("svc", AccountKind.Service, Roles.Reader, Password, "root");

        create.Should().Throw<AccountRejectedException>().WithMessage("*no password*");
    }

    [Fact]
    public void should_refuse_a_name_that_is_taken_whatever_its_case()
    {
        _h.Admin("Alice");

        var create = () => Subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");

        create.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Conflict);
    }

    [Fact]
    public void should_sign_a_user_in_with_the_right_password_whatever_the_case_or_the_spaces_around_the_name()
    {
        var alice = Subject.Create("Alice", AccountKind.User, Roles.Reader, Password, "root");

        Subject.SignIn("  alice  ", Password).Should().Be(alice);
        _h.Audit.Recent(1).Single().Action.Should().Be("login.succeeded");
    }

    [Fact]
    public void should_answer_the_same_for_a_wrong_password_and_an_unknown_name_but_audit_only_the_name_that_exists()
    {
        _h.Reader();

        Subject.SignIn("alice", "wrong password here").Should().BeNull();
        Subject.SignIn("nobody", Password).Should().BeNull();

        _h.Audit.Recent(10, "login.failed").Should().ContainSingle().Which.Target.Should().Be("alice");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void should_refuse_a_name_that_cannot_be_one_without_counting_or_auditing_it(string name)
    {
        Subject.SignIn(name, Password).Should().BeNull();

        _h.Audit.Recent(10).Should().BeEmpty();
    }

    [Fact]
    public void should_refuse_a_name_or_a_password_that_is_too_long_before_hashing_or_counting_anything()
    {
        _h.Reader("alice");

        Subject.SignIn(new string('n', 65), Password).Should().BeNull();
        Subject.SignIn("alice", new string('p', PasswordPolicy.MaxLength + 1)).Should().BeNull();

        _h.Audit.Recent(10, "login.failed").Should().BeEmpty();
        _h.Throttle.IsLocked(new string('n', 65)).Should().BeFalse();
    }

    [Fact]
    public void should_not_let_a_disabled_account_sign_in_even_with_the_right_password()
    {
        _h.Admin();
        var alice = _h.Reader();
        Subject.SetEnabled(alice.Id, false, "root");

        Subject.SignIn("alice", Password).Should().BeNull();
    }

    [Fact]
    public void should_never_sign_a_service_account_in_with_a_password()
    {
        _h.Robot("svc");

        Subject.SignIn("svc", "any password at all").Should().BeNull();
    }

    [Fact]
    public void should_lock_a_name_after_five_failures_even_for_the_right_password_then_let_it_in_later()
    {
        _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Subject.SignIn("alice", "wrong password here").Should().BeNull();
        }

        var locked = () => Subject.SignIn("alice", Password);

        locked.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Locked);
        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        Subject.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_lock_a_name_that_does_not_exist_too_so_that_the_lock_tells_nothing()
    {
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Subject.SignIn("ghost", "wrong password here");
        }

        var locked = () => Subject.SignIn("ghost", Password);

        locked.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Locked);
    }

    [Fact]
    public void should_audit_a_locked_name_once_a_minute_and_not_at_every_attempt()
    {
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Subject.SignIn("ghost", "wrong password here");
        }

        for (var i = 0; i < 20; i++)
        {
            new Action(() => Subject.SignIn("ghost", Password)).Should().Throw<AccountRejectedException>();
        }

        _h.Audit.Recent(100, "login.locked").Should().ContainSingle();
    }

    [Fact]
    public void should_forget_the_failures_of_a_name_that_signs_in()
    {
        _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            Subject.SignIn("alice", "wrong password here");
        }

        Subject.SignIn("alice", Password).Should().NotBeNull();

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            Subject.SignIn("alice", "wrong password here");
        }

        Subject.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_hash_again_with_the_current_cost_when_an_old_hash_is_used_to_sign_in()
    {
        var old = new AccountsHarness(hasher: AccountsHarness.CheapHasher(iterations: 1));
        using (old)
        {
            old.Reader();
            var oldHash = old.Accounts.GetCredentials("alice")!.PasswordHash!;
            var stronger = new AccountService(old.Accounts, old.Audit, old.Throttle, old.Sessions, old.Sampler, AccountsHarness.CheapHasher(iterations: 3), old.Clock);

            stronger.SignIn("alice", Password).Should().NotBeNull();

            old.Accounts.GetCredentials("alice")!.PasswordHash.Should().NotBe(oldHash);
            stronger.SignIn("alice", Password).Should().NotBeNull();
        }
    }

    [Fact]
    public void should_verify_the_current_password_of_a_person_without_signing_in()
    {
        var alice = _h.Reader();

        Subject.VerifyPassword(alice.Id, Password).Should().BeTrue();
        Subject.VerifyPassword(alice.Id, "wrong password here").Should().BeFalse();

        _h.Audit.Recent(100, "login.succeeded").Should().BeEmpty();
        _h.Audit.Recent(100, "password.verify-failed").Should().ContainSingle();
    }

    [Fact]
    public void should_count_the_wrong_current_passwords_like_failed_sign_ins_so_that_a_stolen_session_cannot_guess_the_password()
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Subject.VerifyPassword(alice.Id, "wrong password here").Should().BeFalse();
        }

        var guess = () => Subject.VerifyPassword(alice.Id, Password);

        guess.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Locked);
        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        Subject.VerifyPassword(alice.Id, Password).Should().BeTrue();
    }

    [Fact]
    public void should_clear_the_count_when_the_current_password_is_right()
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            Subject.VerifyPassword(alice.Id, "wrong password here");
        }

        Subject.VerifyPassword(alice.Id, Password).Should().BeTrue();

        _h.Throttle.IsLocked("alice").Should().BeFalse();
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

        update.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Conflict);
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

        change.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Conflict);
        _h.Accounts.CountEnabledAdmins().Should().Be(1);
    }

    [Fact]
    public void should_allow_it_once_another_administrator_is_enabled_and_count_only_enabled_ones()
    {
        var root = _h.Admin();
        var second = _h.Admin("second");

        Subject.SetEnabled(second.Id, false, "x");
        new Action(() => Subject.Delete(root.Id, "x")).Should().Throw<AccountRejectedException>();
        Subject.SetEnabled(second.Id, true, "x");

        Subject.Delete(root.Id, "x");
        _h.Accounts.CountEnabledAdmins().Should().Be(1);
    }

    [Fact]
    public async Task should_keep_an_administrator_when_two_administrators_are_demoted_at_the_same_moment()
    {
        var one = _h.Admin("one");
        var two = _h.Admin("two");

        var attempts = new[] { one.Id, two.Id }.Select(id => Task.Run(() =>
        {
            try
            {
                Subject.ChangeRole(id, Roles.Reader, "x");
            }
            catch (AccountRejectedException)
            {
                // The one that came second is refused: that is the point.
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(attempts);

        _h.Accounts.CountEnabledAdmins().Should().Be(1);
    }

    [Fact]
    public void should_keep_an_administrator_an_administrator_when_the_role_is_set_to_admin_again()
    {
        var root = _h.Admin();

        Subject.ChangeRole(root.Id, Roles.Admin, "x").Role.Should().Be(Roles.Admin);
    }

    [Fact]
    public void should_change_a_password_so_that_only_the_new_one_works_and_clear_a_lock()
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Subject.SignIn("alice", "wrong password here");
        }

        Subject.SetPassword(alice.Id, "another long password", "root");

        Subject.SignIn("alice", Password).Should().BeNull();
        Subject.SignIn("alice", "another long password").Should().NotBeNull();
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

    [Theory]
    [InlineData("short")]
    [InlineData("a-password-of-129-characters-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void should_refuse_a_new_password_that_is_too_short_or_too_long(string password)
    {
        var alice = _h.Reader();

        new Action(() => Subject.SetPassword(alice.Id, password, "x")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
        Subject.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_refuse_a_password_for_a_service_account()
    {
        var svc = _h.Robot();

        new Action(() => Subject.SetPassword(svc.Id, Password, "x")).Should().Throw<AccountRejectedException>().WithMessage("*no password*");
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

        action.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
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

    [Fact]
    public void should_reset_a_password_enable_the_account_lift_its_lock_and_end_its_sessions()
    {
        _h.Admin();
        var alice = _h.Reader();
        Subject.SetEnabled(alice.Id, false, "root");
        var session = _h.Sessions.Start(alice.Id);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Subject.SignIn("alice", "wrong password here");
        }

        Subject.Reset("alice", "a brand new password");

        Subject.SignIn("alice", "a brand new password").Should().NotBeNull();
        _h.Sessions.AccountOf(session).Should().BeNull();
        _h.Audit.Recent(100, "account.reset").Should().ContainSingle().Which.Actor.Should().Be("system");
    }

    [Theory]
    [InlineData("nobody", "a brand new password", AccountProblem.NotFound)]
    [InlineData("agent", "a brand new password", AccountProblem.Invalid)]
    [InlineData("alice", "short", AccountProblem.Invalid)]
    public void should_refuse_a_reset_of_nobody_of_a_service_or_to_a_weak_password(string name, string password, AccountProblem problem)
    {
        _h.Reader();
        _h.Robot();

        new Action(() => Subject.Reset(name, password)).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(problem);
    }
}
