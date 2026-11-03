using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AccountServiceFixture : IDisposable
{
    private const string Password = "correct horse battery";
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteAccountStore _accounts;
    private readonly SqliteAuditLog _audit;
    private readonly AccountService _subject;

    public AccountServiceFixture()
    {
        _accounts = new SqliteAccountStore(_workspace.Database);
        _audit = new SqliteAuditLog(_workspace.Database, _clock);
        _subject = new AccountService(_accounts, _audit, new LoginThrottle(_clock), _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private Account Admin(string name = "root") => _subject.Create(name, AccountKind.User, Roles.Admin, Password, "test");

    [Fact]
    public void should_create_a_user_with_a_hashed_password_and_audit_it()
    {
        var account = _subject.Create("  alice  ", AccountKind.User, Roles.Reader, Password, "root");

        account.Should().Match<Account>(a => a.Name == "alice" && a.Kind == AccountKind.User && a.Role == Roles.Reader && a.Enabled && a.CreatedAt == _clock.GetUtcNow());
        var stored = _accounts.GetCredentials("alice")!;
        stored.PasswordHash.Should().NotBeNullOrEmpty().And.NotContain(Password);
        _audit.Recent(10).Should().ContainSingle().Which.Should().Match<AuditEvent>(e => e.Actor == "root" && e.Action == "account.created" && e.Target == "alice");
    }

    [Fact]
    public void should_create_a_service_account_without_a_password()
    {
        var account = _subject.Create("collector-laptop", AccountKind.Service, Roles.Collector, null, "root");

        _accounts.GetCredentials("collector-laptop")!.PasswordHash.Should().BeNull();
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
        var create = () => _subject.Create(name, AccountKind.User, Roles.Reader, Password, "root");

        create.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
    }

    [Theory]
    [InlineData(AccountKind.User, Roles.Assistant)]
    [InlineData(AccountKind.User, Roles.Collector)]
    [InlineData(AccountKind.Service, Roles.Admin)]
    [InlineData(AccountKind.User, "Nobody")]
    public void should_refuse_a_role_that_does_not_exist_or_is_not_for_that_kind_of_account(AccountKind kind, string role)
    {
        var create = () => _subject.Create("someone", kind, role, kind == AccountKind.User ? Password : null, "root");

        create.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
    }

    [Theory]
    [InlineData(null, "needs a password")]
    [InlineData("short", "at least 12")]
    [InlineData("alice-alice", "at least 12")]
    public void should_refuse_a_user_without_a_good_password(string? password, string reason)
    {
        var create = () => _subject.Create("alice", AccountKind.User, Roles.Reader, password, "root");

        create.Should().Throw<AccountRejectedException>().WithMessage($"*{reason}*");
    }

    [Fact]
    public void should_refuse_a_password_that_is_the_name_of_the_account()
    {
        var create = () => _subject.Create("alice-the-user", AccountKind.User, Roles.Reader, "ALICE-THE-USER", "root");

        create.Should().Throw<AccountRejectedException>().WithMessage("*name of the account*");
    }

    [Fact]
    public void should_refuse_a_service_account_that_is_given_a_password()
    {
        var create = () => _subject.Create("svc", AccountKind.Service, Roles.Reader, Password, "root");

        create.Should().Throw<AccountRejectedException>().WithMessage("*no password*");
    }

    [Fact]
    public void should_refuse_a_name_that_is_taken_whatever_its_case()
    {
        Admin("Alice");

        var create = () => _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");

        create.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Conflict);
    }

    [Fact]
    public void should_sign_a_user_in_with_the_right_password_whatever_the_case_of_the_name()
    {
        var alice = _subject.Create("Alice", AccountKind.User, Roles.Reader, Password, "root");

        _subject.SignIn("alice", Password).Should().Be(alice);
        _audit.Recent(1).Single().Action.Should().Be("login.succeeded");
    }

    [Theory]
    [InlineData("alice", "wrong password here")]
    [InlineData("nobody", Password)]
    public void should_answer_the_same_for_a_wrong_password_and_an_unknown_name(string name, string password)
    {
        _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");

        _subject.SignIn(name, password).Should().BeNull();
        _audit.Recent(1).Single().Action.Should().Be("login.failed");
    }

    [Fact]
    public void should_not_let_a_disabled_account_sign_in_even_with_the_right_password()
    {
        var alice = _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");
        Admin();
        _subject.SetEnabled(alice.Id, false, "root");

        _subject.SignIn("alice", Password).Should().BeNull();
    }

    [Fact]
    public void should_never_sign_a_service_account_in_with_a_password()
    {
        _subject.Create("svc", AccountKind.Service, Roles.Reader, null, "root");

        _subject.SignIn("svc", string.Empty).Should().BeNull();
        _subject.SignIn("svc", Password).Should().BeNull();
    }

    [Fact]
    public void should_lock_a_name_after_five_failures_even_for_the_right_password_then_let_it_in_later()
    {
        _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");
        for (var i = 0; i < 5; i++)
        {
            _subject.SignIn("alice", "wrong password here").Should().BeNull();
        }

        var locked = () => _subject.SignIn("alice", Password);

        locked.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Locked);
        _clock.Advance(TimeSpan.FromMinutes(16));
        _subject.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_lock_a_name_that_does_not_exist_too_so_that_the_lock_tells_nothing()
    {
        for (var i = 0; i < 5; i++)
        {
            _subject.SignIn("ghost", "wrong password here");
        }

        var locked = () => _subject.SignIn("ghost", Password);

        locked.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Locked);
    }

    [Fact]
    public void should_forget_the_failures_of_a_name_that_signs_in()
    {
        _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");
        for (var i = 0; i < 4; i++)
        {
            _subject.SignIn("alice", "wrong password here");
        }

        _subject.SignIn("alice", Password).Should().NotBeNull();
        for (var i = 0; i < 4; i++)
        {
            _subject.SignIn("alice", "wrong password here");
        }

        _subject.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_change_a_role_and_audit_it()
    {
        Admin();
        var alice = _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");

        _subject.ChangeRole(alice.Id, Roles.Operator, "root").Role.Should().Be(Roles.Operator);

        _accounts.Get(alice.Id)!.Role.Should().Be(Roles.Operator);
        _audit.Recent(1).Single().Should().Match<AuditEvent>(e => e.Action == "account.role-changed" && e.Detail == "Reader to Operator");
    }

    [Fact]
    public void should_refuse_to_demote_disable_or_delete_the_last_enabled_administrator()
    {
        var root = Admin();

        new Action[]
        {
            () => _subject.ChangeRole(root.Id, Roles.Reader, "x"),
            () => _subject.SetEnabled(root.Id, false, "x"),
            () => _subject.Delete(root.Id, "x"),
        }.Should().AllSatisfy(action => action.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Conflict));
        _accounts.CountEnabledAdmins().Should().Be(1);
    }

    [Fact]
    public void should_allow_it_once_another_administrator_is_enabled_and_count_only_enabled_ones()
    {
        var root = Admin();
        var second = Admin("second");

        _subject.SetEnabled(second.Id, false, "x");
        new Action(() => _subject.Delete(root.Id, "x")).Should().Throw<AccountRejectedException>();
        _subject.SetEnabled(second.Id, true, "x");

        _subject.Delete(root.Id, "x");
        _accounts.CountEnabledAdmins().Should().Be(1);
    }

    [Fact]
    public void should_keep_an_administrator_an_administrator_when_the_role_is_set_to_admin_again()
    {
        var root = Admin();

        _subject.ChangeRole(root.Id, Roles.Admin, "x").Role.Should().Be(Roles.Admin);
    }

    [Fact]
    public void should_change_a_password_so_that_only_the_new_one_works_and_clear_a_lock()
    {
        var alice = _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");
        for (var i = 0; i < 5; i++)
        {
            _subject.SignIn("alice", "wrong password here");
        }

        _subject.SetPassword(alice.Id, "another long password", "root");

        _subject.SignIn("alice", Password).Should().BeNull();
        _subject.SignIn("alice", "another long password").Should().NotBeNull();
    }

    [Fact]
    public void should_refuse_a_weak_new_password_and_a_password_for_a_service_account()
    {
        var alice = _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");
        var svc = _subject.Create("svc", AccountKind.Service, Roles.Reader, null, "root");

        new Action(() => _subject.SetPassword(alice.Id, "short", "x")).Should().Throw<AccountRejectedException>();
        new Action(() => _subject.SetPassword(svc.Id, Password, "x")).Should().Throw<AccountRejectedException>().WithMessage("*no password*");
        _subject.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_say_there_is_no_such_account_for_an_unknown_id()
    {
        var id = Guid.NewGuid();

        new Action[]
        {
            () => _subject.ChangeRole(id, Roles.Reader, "x"),
            () => _subject.SetEnabled(id, true, "x"),
            () => _subject.SetPassword(id, Password, "x"),
            () => _subject.Delete(id, "x"),
        }.Should().AllSatisfy(action => action.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound));
    }

    [Fact]
    public void should_enable_an_account_again_and_audit_both_changes()
    {
        Admin();
        var alice = _subject.Create("alice", AccountKind.User, Roles.Reader, Password, "root");

        _subject.SetEnabled(alice.Id, false, "root").Enabled.Should().BeFalse();
        _subject.SetEnabled(alice.Id, true, "root").Enabled.Should().BeTrue();

        _audit.Recent(2).Select(e => e.Action).Should().Equal("account.enabled", "account.disabled");
    }

    [Fact]
    public void should_report_whether_a_role_change_or_deletion_hit_an_account_that_exists()
    {
        _accounts.Update(Guid.NewGuid(), Roles.Reader, true).Should().BeFalse();
        _accounts.SetPasswordHash(Guid.NewGuid(), "x").Should().BeFalse();
        _accounts.Remove(Guid.NewGuid()).Should().BeFalse();
        _accounts.Get(Guid.NewGuid()).Should().BeNull();
        _accounts.GetCredentials("nobody").Should().BeNull();
        _accounts.Count().Should().Be(0);
    }
}
