using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Test.Accounts;

public sealed class ApiKeyServiceFixture : IDisposable
{
    private readonly AccountsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private ApiKeyService Subject => _h.ApiKeys;

    [Fact]
    public void should_create_a_key_whose_secret_is_shown_once_and_never_stored()
    {
        var account = _h.Robot();

        var created = Subject.Create(account.Id, " laptop ", null, null, "root");

        created.Secret.Should().StartWith("mnm_").And.HaveLength(4 + 43);
        var expectedPrefix = created.Secret[..12];
        created.Key.Should().Match<ApiKey>(key => key.Name == "laptop" && key.Prefix == expectedPrefix && key.AccountId == account.Id && key.RevokedAt == null);
        using var connection = _h.Workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM api_keys WHERE secret_hash = $secret OR prefix = $secret";
        command.Parameters.AddWithValue("$secret", created.Secret);
        command.ExecuteScalar().Should().Be(0L);
        _h.Audit.Recent(1).Single().Should().Match<AuditEvent>(e => e.Action == "key.created" && !e.Detail.Contains(created.Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void should_refuse_a_key_for_a_user_whatever_their_role_because_a_person_signs_in_with_a_password()
    {
        var root = _h.Admin();

        new Action(() => Subject.Create(root.Id, "script", null, null, "root")).Should().Throw<AccountRejectedException>()
            .Which.Should().Match<AccountRejectedException>(e => e.Problem == AccountProblem.Invalid && e.Message.Contains("service account"));
    }

    [Fact]
    public void should_refuse_a_key_that_is_found_on_a_user_account_even_if_it_was_stored_there()
    {
        var alice = _h.Reader();
        var key = new ApiKey(Guid.NewGuid(), alice.Id, "old", "mnm_stored___", null, _h.Clock.GetUtcNow(), null, null, null);
        _h.Keys.Add(key, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("mnm_stored-on-a-user-account"u8)));

        Subject.Authenticate("mnm_stored-on-a-user-account").Should().BeNull();
    }

    [Fact]
    public void should_authenticate_a_key_as_its_account_with_what_the_role_gives()
    {
        var account = _h.Robot("collector", Roles.Collector);
        var created = Subject.Create(account.Id, "collector", null, null, "root");

        var principal = Subject.Authenticate(created.Secret)!;

        principal.Account.Should().Be(account);
        principal.Permissions.Should().BeEquivalentTo(Permissions.IngestWrite);
        principal.KeyId.Should().Be(created.Key.Id);
        principal.Sources.Should().BeNull();
    }

    [Fact]
    public void should_carry_the_sources_a_key_is_limited_to()
    {
        var account = _h.Robot();
        var allowed = Guid.NewGuid();

        var principal = Subject.Authenticate(Subject.Create(account.Id, "k", [allowed, allowed], null, "root").Secret)!;

        principal.Sources.Should().Equal(allowed);
        principal.CanAccess(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void should_refuse_a_key_limited_to_no_source_at_all_because_it_would_be_read_as_all_of_them_elsewhere()
    {
        var account = _h.Robot();

        new Action(() => Subject.Create(account.Id, "k", [], null, "root")).Should().Throw<AccountRejectedException>().WithMessage("*at least one*");
    }

    [Fact]
    public void should_follow_a_change_of_role_at_once_so_that_a_key_never_exceeds_its_account()
    {
        var account = _h.Robot("someone", Roles.Reader);
        var secret = Subject.Create(account.Id, "k", null, null, "root").Secret;
        Subject.Authenticate(secret)!.Can(Permissions.NotesWrite).Should().BeFalse();

        _h.Service.ChangeRole(account.Id, Roles.Assistant, "root");

        Subject.Authenticate(secret)!.Can(Permissions.NotesWrite).Should().BeTrue();
        Subject.Authenticate(secret)!.Can(Permissions.AccountsManage).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("sk-other-prefix")]
    [InlineData("mnm_not-a-real-key")]
    public void should_refuse_a_secret_that_is_not_a_key_of_ours(string secret)
    {
        Subject.Authenticate(secret).Should().BeNull();
    }

    [Fact]
    public void should_audit_a_refused_key_by_its_prefix_only()
    {
        Subject.Authenticate("mnm_0123456789abcdefSECRETSECRETSECRET").Should().BeNull();

        var refused = _h.Audit.Recent(1, "key.refused").Single();
        refused.Target.Should().Be("mnm_01234567");
        refused.Detail.Should().BeEmpty();
    }

    [Fact]
    public void should_audit_the_same_refused_prefix_once_a_minute_and_all_refusals_up_to_a_ceiling()
    {
        for (var i = 0; i < 10; i++)
        {
            Subject.Authenticate("mnm_same-prefix-garbage");
        }

        _h.Audit.Recent(100, "key.refused").Should().ContainSingle();
        for (var i = 0; i < AuditSampler.MaxPerMinute + 40; i++)
        {
            Subject.Authenticate($"mnm_{i:D8}-different-prefix");
        }

        _h.Audit.Recent(500, "key.refused").Should().HaveCount(AuditSampler.MaxPerMinute);
    }

    [Fact]
    public void should_refuse_a_revoked_key_and_say_so_when_it_is_revoked_again()
    {
        var account = _h.Robot();
        var created = Subject.Create(account.Id, "k", null, null, "root");

        Subject.Revoke(account.Id, created.Key.Id, "root");

        Subject.Authenticate(created.Secret).Should().BeNull();
        Subject.List(account.Id).Single().RevokedAt.Should().Be(_h.Clock.GetUtcNow());
        new Action(() => Subject.Revoke(account.Id, created.Key.Id, "root")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
    }

    [Fact]
    public void should_not_revoke_the_key_of_another_account()
    {
        var one = _h.Robot();
        var other = _h.Robot("other", Roles.Reader);
        var created = Subject.Create(one.Id, "k", null, null, "root");

        new Action(() => Subject.Revoke(other.Id, created.Key.Id, "root")).Should().Throw<AccountRejectedException>();

        Subject.Authenticate(created.Secret).Should().NotBeNull();
    }

    [Fact]
    public void should_refuse_a_key_that_has_expired_and_accept_it_until_then()
    {
        var account = _h.Robot();
        var created = Subject.Create(account.Id, "k", null, _h.Clock.GetUtcNow().AddHours(1), "root");
        Subject.Authenticate(created.Secret).Should().NotBeNull();

        _h.Clock.Advance(TimeSpan.FromHours(1));

        Subject.Authenticate(created.Secret).Should().BeNull();
    }

    [Fact]
    public void should_refuse_a_key_while_its_account_is_disabled_and_after_it_is_deleted()
    {
        _h.Admin();
        var account = _h.Robot();
        var secret = Subject.Create(account.Id, "k", null, null, "root").Secret;

        _h.Service.SetEnabled(account.Id, false, "root");
        Subject.Authenticate(secret).Should().BeNull();
        _h.Service.SetEnabled(account.Id, true, "root");
        Subject.Authenticate(secret).Should().NotBeNull();

        _h.Service.Delete(account.Id, "root");
        Subject.Authenticate(secret).Should().BeNull();
    }

    [Fact]
    public void should_record_the_last_use_at_most_once_a_minute()
    {
        var account = _h.Robot();
        var created = Subject.Create(account.Id, "k", null, null, "root");
        Subject.List(account.Id).Single().LastUsedAt.Should().BeNull();

        Subject.Authenticate(created.Secret);
        var first = _h.Clock.GetUtcNow();
        _h.Clock.Advance(TimeSpan.FromSeconds(30));
        Subject.Authenticate(created.Secret);
        Subject.List(account.Id).Single().LastUsedAt.Should().Be(first);

        _h.Clock.Advance(TimeSpan.FromSeconds(31));
        Subject.Authenticate(created.Secret);
        Subject.List(account.Id).Single().LastUsedAt.Should().Be(_h.Clock.GetUtcNow());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void should_refuse_a_key_without_a_name(string name)
    {
        var account = _h.Robot();

        new Action(() => Subject.Create(account.Id, name, null, null, "root")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
    }

    [Fact]
    public void should_refuse_a_key_name_that_is_too_long()
    {
        var account = _h.Robot();
        var name = new string('k', 101);

        new Action(() => Subject.Create(account.Id, name, null, null, "root")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
    }

    [Fact]
    public void should_refuse_an_expiry_that_is_not_in_the_future()
    {
        var account = _h.Robot();

        new Action(() => Subject.Create(account.Id, "k", null, _h.Clock.GetUtcNow(), "root")).Should().Throw<AccountRejectedException>().WithMessage("*past*");
    }

    [Fact]
    public void should_say_there_is_no_such_account_for_a_key_to_create_or_to_revoke()
    {
        new Action(() => Subject.Create(Guid.NewGuid(), "k", null, null, "root")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
        new Action(() => Subject.Revoke(Guid.NewGuid(), Guid.NewGuid(), "root")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
    }

    [Fact]
    public void should_remove_the_keys_with_their_account()
    {
        _h.Admin();
        var account = _h.Robot();
        Subject.Create(account.Id, "k", null, null, "root");

        _h.Service.Delete(account.Id, "root");

        using var connection = _h.Workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM api_keys";
        command.ExecuteScalar().Should().Be(0L);
    }
}
