using Microsoft.AspNetCore.Identity;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Test.Accounts;

public sealed class SignInServiceFixture : IDisposable
{
    private const string Password = AccountsHarness.Password;
    private readonly AccountsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private AccountService Subject => _h.Service;
    private SignInService Credentials => _h.SignInService;


    [Fact]
    public void should_sign_a_user_in_with_the_right_password_whatever_the_case_or_the_spaces_around_the_name()
    {
        var alice = Subject.Create("Alice", AccountKind.User, Roles.Reader, Password, "root");

        Credentials.SignIn("  alice  ", Password).Should().Be(alice);
        _h.Audit.Recent(1).Single().Action.Should().Be("login.succeeded");
    }

    [Fact]
    public void should_answer_the_same_for_a_wrong_password_and_an_unknown_name_but_audit_only_the_name_that_exists()
    {
        _h.Reader();

        Credentials.SignIn("alice", "wrong password here").Should().BeNull();
        Credentials.SignIn("nobody", Password).Should().BeNull();

        _h.Audit.Recent(10, "login.failed").Should().ContainSingle().Which.Target.Should().Be("alice");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void should_refuse_a_name_that_cannot_be_one_without_counting_or_auditing_it(string name)
    {
        Credentials.SignIn(name, Password).Should().BeNull();

        _h.Audit.Recent(10).Should().BeEmpty();
    }

    [Fact]
    public void should_refuse_a_name_or_a_password_that_is_too_long_before_hashing_or_counting_anything()
    {
        _h.Reader("alice");

        Credentials.SignIn(new string('n', 65), Password).Should().BeNull();
        Credentials.SignIn("alice", new string('p', PasswordPolicy.MaxLength + 1)).Should().BeNull();

        _h.Audit.Recent(10, "login.failed").Should().BeEmpty();
        _h.Throttle.IsLocked(new string('n', 65)).Should().BeFalse();
    }

    [Fact]
    public void should_not_let_a_disabled_account_sign_in_even_with_the_right_password()
    {
        _h.Admin();
        var alice = _h.Reader();
        Subject.SetEnabled(alice.Id, false, "root");

        Credentials.SignIn("alice", Password).Should().BeNull();
    }

    [Fact]
    public void should_never_sign_a_service_account_in_with_a_password()
    {
        _h.Robot("svc");

        Credentials.SignIn("svc", "any password at all").Should().BeNull();
    }

    [Fact]
    public void should_lock_a_name_after_five_failures_even_for_the_right_password_then_let_it_in_later()
    {
        _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Credentials.SignIn("alice", "wrong password here").Should().BeNull();
        }

        var locked = () => Credentials.SignIn("alice", Password);

        locked.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Locked);
        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        Credentials.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_lock_a_name_that_does_not_exist_too_so_that_the_lock_tells_nothing()
    {
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Credentials.SignIn("ghost", "wrong password here");
        }

        var locked = () => Credentials.SignIn("ghost", Password);

        locked.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Locked);
    }

    [Fact]
    public void should_audit_a_locked_name_once_a_minute_and_not_at_every_attempt()
    {
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Credentials.SignIn("ghost", "wrong password here");
        }

        for (var i = 0; i < 20; i++)
        {
            new Action(() => Credentials.SignIn("ghost", Password)).Should().Throw<AccountRejectedException>();
        }

        _h.Audit.Recent(100, "login.locked").Should().ContainSingle();
    }

    [Fact]
    public void should_forget_the_failures_of_a_name_that_signs_in()
    {
        _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            Credentials.SignIn("alice", "wrong password here");
        }

        Credentials.SignIn("alice", Password).Should().NotBeNull();

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            Credentials.SignIn("alice", "wrong password here");
        }

        Credentials.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_hash_again_with_the_current_cost_when_an_old_hash_is_used_to_sign_in()
    {
        var old = new AccountsHarness(hasher: AccountsHarness.CheapHasher(iterations: 1));
        using (old)
        {
            old.Reader();
            var oldHash = old.Accounts.GetCredentials("alice")!.PasswordHash!;
            var stronger = new SignInService(old.Accounts, old.Audit, old.Throttle, old.Sessions, old.Sampler, AccountsHarness.CheapHasher(iterations: 3));

            stronger.SignIn("alice", Password).Should().NotBeNull();

            old.Accounts.GetCredentials("alice")!.PasswordHash.Should().NotBe(oldHash);
            stronger.SignIn("alice", Password).Should().NotBeNull();
        }
    }

    [Fact]
    public void should_verify_the_current_password_of_a_person_without_signing_in()
    {
        var alice = _h.Reader();

        Credentials.VerifyPassword(alice.Id, Password).Should().BeTrue();
        Credentials.VerifyPassword(alice.Id, "wrong password here").Should().BeFalse();

        _h.Audit.Recent(100, "login.succeeded").Should().BeEmpty();
        _h.Audit.Recent(100, "password.verify-failed").Should().ContainSingle();
    }

    [Fact]
    public void should_refuse_a_current_password_for_an_account_that_has_none_and_one_that_is_too_long_and_audit_both()
    {
        var robot = _h.Robot("svc");
        var alice = _h.Reader();

        Credentials.VerifyPassword(robot.Id, Password).Should().BeFalse();
        Credentials.VerifyPassword(alice.Id, new string('p', PasswordPolicy.MaxLength + 1)).Should().BeFalse();

        _h.Audit.Recent(10, "password.verify-failed").Should().HaveCount(2);
    }

    [Fact]
    public void should_count_the_wrong_current_passwords_like_failed_sign_ins_so_that_a_stolen_session_cannot_guess_the_password()
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Credentials.VerifyPassword(alice.Id, "wrong password here").Should().BeFalse();
        }

        var guess = () => Credentials.VerifyPassword(alice.Id, Password);

        guess.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Locked);
        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        Credentials.VerifyPassword(alice.Id, Password).Should().BeTrue();
    }

    [Fact]
    public void should_clear_the_count_when_the_current_password_is_right()
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            Credentials.VerifyPassword(alice.Id, "wrong password here");
        }

        Credentials.VerifyPassword(alice.Id, Password).Should().BeTrue();

        _h.Throttle.IsLocked(SignInService.PasswordCheckKey("alice")).Should().BeFalse();
    }

    [Fact]
    public void should_not_let_wrong_current_passwords_lock_the_sign_in_of_the_person()
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Credentials.VerifyPassword(alice.Id, "wrong password here");
        }

        Credentials.SignIn("alice", Password).Should().NotBeNull();
    }

    [Fact]
    public void should_not_let_wrong_sign_ins_by_anybody_lock_the_person_out_of_changing_their_password()
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Credentials.SignIn("alice", "wrong password here");
        }

        Credentials.VerifyPassword(alice.Id, Password).Should().BeTrue();
    }

    [Fact]
    public async Task should_sign_a_user_in_and_start_a_session_for_them()
    {
        var alice = _h.Reader();

        var signedIn = await Credentials.SignInAsync("alice", Password, TestContext.Current.CancellationToken);

        signedIn!.Account.Id.Should().Be(alice.Id);
        _h.Sessions.AccountOf(signedIn.SessionId).Should().Be(alice.Id);
    }

    [Fact]
    public async Task should_leave_no_session_behind_when_the_password_was_changed_while_the_old_one_was_being_checked()
    {
        using var hasher = new BlockingHasher();
        using var harness = new AccountsHarness(hasher: hasher);
        var alice = harness.Reader();
        var token = TestContext.Current.CancellationToken;
        hasher.Block();
        var signingIn = Task.Run(() => harness.SignInService.SignInAsync("alice", Password, token), token);
        hasher.WaitUntilEntered(1);

        harness.Service.SetPassword(alice.Id, "another long password", "root");
        hasher.Release();

        (await signingIn).Should().BeNull("the password that was checked is not the password any more");
        CountSessions(harness).Should().Be(0);
    }

    [Fact]
    public async Task should_leave_no_session_behind_when_the_account_was_disabled_while_the_password_was_being_checked()
    {
        using var hasher = new BlockingHasher();
        using var harness = new AccountsHarness(hasher: hasher);
        harness.Admin();
        var alice = harness.Reader();
        var token = TestContext.Current.CancellationToken;
        hasher.Block();
        var signingIn = Task.Run(() => harness.SignInService.SignInAsync("alice", Password, token), token);
        hasher.WaitUntilEntered(1);

        harness.Service.SetEnabled(alice.Id, false, "root");
        hasher.Release();

        (await signingIn).Should().BeNull();
        CountSessions(harness).Should().Be(0);
    }

    private static long CountSessions(AccountsHarness harness)
    {
        using var connection = harness.Workspace.Database.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sessions";
        return (long)count.ExecuteScalar()!;
    }

    [Fact]
    public async Task should_hash_at_most_a_few_passwords_at_a_time_and_tell_the_others_to_come_back_later_without_holding_a_thread()
    {
        using var hasher = new BlockingHasher();
        using var harness = new AccountsHarness(hasher: hasher, hashWait: TimeSpan.Zero);
        harness.Reader();
        var subject = harness.SignInService;
        hasher.Block();
        var token = TestContext.Current.CancellationToken;

        var running = Enumerable.Range(0, SignInService.MaxConcurrentHashes)
            .Select(i => Task.Run(() => subject.SignInAsync($"someone-{i}", Password, token), token))
            .ToList();
        hasher.WaitUntilEntered(SignInService.MaxConcurrentHashes);
        var overflow = async () => await subject.SignInAsync("alice", Password, token);

        (await overflow.Should().ThrowAsync<AccountRejectedException>()).Which.Problem.Should().Be(AccountProblem.Busy);
        hasher.Release();
        await Task.WhenAll(running);
        (await subject.SignInAsync("alice", Password, token)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("password-check:alice")]
    [InlineData("al ice")]
    [InlineData("a")]
    [InlineData("system")]
    [InlineData("Unknown")]
    public void should_refuse_what_a_name_cannot_be_without_counting_it_so_that_a_forged_name_reaches_no_other_budget(string name)
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures + 2; i++)
        {
            Credentials.SignIn(name, "wrong password here").Should().BeNull();
        }

        Credentials.VerifyPassword(alice.Id, Password).Should().BeTrue();
        _h.Throttle.IsLocked(SignInService.PasswordCheckKey("alice")).Should().BeFalse();
    }

    [Fact]
    public void should_change_a_password_so_that_only_the_new_one_works_and_clear_a_lock()
    {
        var alice = _h.Reader();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Credentials.SignIn("alice", "wrong password here");
        }

        Subject.SetPassword(alice.Id, "another long password", "root");

        Credentials.SignIn("alice", Password).Should().BeNull();
        Credentials.SignIn("alice", "another long password").Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void should_refuse_a_new_password_that_is_too_short_or_too_long(bool tooLong)
    {
        var password = tooLong ? new string('p', PasswordPolicy.MaxLength + 1) : "short";
        var alice = _h.Reader();

        new Action(() => Subject.SetPassword(alice.Id, password, "x")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
        Credentials.SignIn("alice", Password).Should().NotBeNull();
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
            Credentials.SignIn("alice", "wrong password here");
        }

        Subject.Reset("alice", "a brand new password");

        Credentials.SignIn("alice", "a brand new password").Should().NotBeNull();
        _h.Sessions.AccountOf(session).Should().BeNull();
        _h.Audit.Recent(100, "account.reset").Should().ContainSingle().Which.Actor.Should().Be("system");
    }

    /// <summary>A hasher whose checks wait until the test lets them go, to hold the places of the server.</summary>
    private sealed class BlockingHasher : Microsoft.AspNetCore.Identity.IPasswordHasher<Account>, IDisposable
    {
        private readonly ManualResetEventSlim _open = new(initialState: true);
        private readonly SemaphoreSlim _entered = new(0);

        public void Block() => _open.Reset();

        public void Release() => _open.Set();

        public void WaitUntilEntered(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the checks reach the hasher");
            }
        }

        /// <summary>A test that fails first must not leave threads parked on the gate.</summary>
        public void Dispose()
        {
            _open.Set();
            _open.Dispose();
            _entered.Dispose();
        }

        public string HashPassword(Account account, string password) => "hash:" + password;

        public Microsoft.AspNetCore.Identity.PasswordVerificationResult VerifyHashedPassword(Account account, string hashedPassword, string providedPassword)
        {
            _entered.Release();
            _open.Wait(TimeSpan.FromSeconds(10));
            return hashedPassword == "hash:" + providedPassword
                ? Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success
                : Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;
        }
    }

    [Theory]
    [InlineData("ab\n", false)]
    [InlineData("ab", true)]
    [InlineData("a", false)]
    [InlineData("system", false)]
    public void should_know_what_a_name_is_down_to_the_last_character(string name, bool valid)
    {
        AccountNames.IsValid(name).Should().Be(valid);
    }
}
