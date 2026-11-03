using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class ApiKeyServiceFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteAccountStore _accounts;
    private readonly SqliteApiKeyStore _keys;
    private readonly SqliteAuditLog _audit;
    private readonly AccountService _accountService;
    private readonly ApiKeyService _subject;

    public ApiKeyServiceFixture()
    {
        _accounts = new SqliteAccountStore(_workspace.Database);
        _keys = new SqliteApiKeyStore(_workspace.Database);
        _audit = new SqliteAuditLog(_workspace.Database, _clock);
        _accountService = new AccountService(_accounts, _audit, new LoginThrottle(_clock), _clock);
        _subject = new ApiKeyService(_accounts, _keys, _audit, _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private Account Service(string role = Roles.Assistant) => _accountService.Create("agent", AccountKind.Service, role, null, "root");

    [Fact]
    public void should_create_a_key_whose_secret_is_shown_once_and_never_stored()
    {
        var account = Service();

        var created = _subject.Create(account.Id, " laptop ", null, null, "root");

        created.Secret.Should().StartWith("mnm_").And.HaveLength(4 + 43);
        var expectedPrefix = created.Secret[..12];
        created.Key.Should().Match<ApiKey>(key => key.Name == "laptop" && key.Prefix == expectedPrefix && key.AccountId == account.Id && key.RevokedAt == null);
        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM api_keys WHERE secret_hash = $secret OR prefix = $secret";
        command.Parameters.AddWithValue("$secret", created.Secret);
        command.ExecuteScalar().Should().Be(0L);
        _audit.Recent(1).Single().Should().Match<AuditEvent>(e => e.Action == "key.created" && !e.Detail.Contains(created.Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void should_give_every_key_a_secret_of_its_own()
    {
        var account = Service();

        Enumerable.Range(0, 20).Select(_ => _subject.Create(account.Id, "k", null, null, "root").Secret).Distinct().Should().HaveCount(20);
    }

    [Fact]
    public void should_authenticate_a_key_as_its_account_with_what_the_role_gives()
    {
        var account = Service(Roles.Collector);
        var created = _subject.Create(account.Id, "collector", null, null, "root");

        var principal = _subject.Authenticate(created.Secret)!;

        principal.Account.Should().Be(account);
        principal.Permissions.Should().BeEquivalentTo(Permissions.IngestWrite);
        principal.KeyId.Should().Be(created.Key.Id);
        principal.Sources.Should().BeNull();
    }

    [Fact]
    public void should_carry_the_sources_a_key_is_limited_to()
    {
        var account = Service();
        var allowed = Guid.NewGuid();

        var principal = _subject.Authenticate(_subject.Create(account.Id, "k", [allowed, allowed], null, "root").Secret)!;

        principal.Sources.Should().Equal(allowed);
        principal.CanAccess(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void should_follow_a_change_of_role_at_once_so_that_a_key_never_exceeds_its_account()
    {
        var account = _accountService.Create("someone", AccountKind.Service, Roles.Reader, null, "root");
        var secret = _subject.Create(account.Id, "k", null, null, "root").Secret;
        _subject.Authenticate(secret)!.Can(Permissions.NotesWrite).Should().BeFalse();

        _accountService.ChangeRole(account.Id, Roles.Assistant, "root");

        _subject.Authenticate(secret)!.Can(Permissions.NotesWrite).Should().BeTrue();
        _subject.Authenticate(secret)!.Can(Permissions.AccountsManage).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("mnm_")]
    [InlineData("mnm_not-a-real-key")]
    [InlineData("Bearer something")]
    [InlineData("sk-other-prefix")]
    public void should_refuse_a_secret_that_is_not_a_key_of_ours(string secret)
    {
        _subject.Authenticate(secret).Should().BeNull();
    }

    [Fact]
    public void should_audit_a_refused_key_by_its_prefix_only()
    {
        _subject.Authenticate("mnm_0123456789abcdefSECRETSECRETSECRET").Should().BeNull();

        var refused = _audit.Recent(1, "key.refused").Single();
        refused.Target.Should().Be("mnm_01234567");
        refused.Detail.Should().BeEmpty();
    }

    [Fact]
    public void should_refuse_a_revoked_key_and_say_so_when_it_is_revoked_again()
    {
        var account = Service();
        var created = _subject.Create(account.Id, "k", null, null, "root");

        _subject.Revoke(account.Id, created.Key.Id, "root");

        _subject.Authenticate(created.Secret).Should().BeNull();
        _subject.List(account.Id).Single().RevokedAt.Should().Be(_clock.GetUtcNow());
        new Action(() => _subject.Revoke(account.Id, created.Key.Id, "root")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
    }

    [Fact]
    public void should_not_revoke_the_key_of_another_account()
    {
        var one = Service();
        var other = _accountService.Create("other", AccountKind.Service, Roles.Reader, null, "root");
        var created = _subject.Create(one.Id, "k", null, null, "root");

        new Action(() => _subject.Revoke(other.Id, created.Key.Id, "root")).Should().Throw<AccountRejectedException>();

        _subject.Authenticate(created.Secret).Should().NotBeNull();
    }

    [Fact]
    public void should_refuse_a_key_that_has_expired_and_accept_it_until_then()
    {
        var account = Service();
        var created = _subject.Create(account.Id, "k", null, _clock.GetUtcNow().AddHours(1), "root");
        _subject.Authenticate(created.Secret).Should().NotBeNull();

        _clock.Advance(TimeSpan.FromHours(1));

        _subject.Authenticate(created.Secret).Should().BeNull();
    }

    [Fact]
    public void should_refuse_a_key_whose_account_is_disabled_or_deleted()
    {
        _accountService.Create("root", AccountKind.User, Roles.Admin, "correct horse battery", "test");
        var account = Service();
        var secret = _subject.Create(account.Id, "k", null, null, "root").Secret;

        _accountService.SetEnabled(account.Id, false, "root");
        _subject.Authenticate(secret).Should().BeNull();
        _accountService.SetEnabled(account.Id, true, "root");
        _subject.Authenticate(secret).Should().NotBeNull();

        _accountService.Delete(account.Id, "root");
        _subject.Authenticate(secret).Should().BeNull();
    }

    [Fact]
    public void should_record_the_last_use_at_most_once_a_minute()
    {
        var account = Service();
        var created = _subject.Create(account.Id, "k", null, null, "root");
        _subject.List(account.Id).Single().LastUsedAt.Should().BeNull();

        _subject.Authenticate(created.Secret);
        var first = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromSeconds(30));
        _subject.Authenticate(created.Secret);
        _subject.List(account.Id).Single().LastUsedAt.Should().Be(first);

        _clock.Advance(TimeSpan.FromSeconds(31));
        _subject.Authenticate(created.Secret);
        _subject.List(account.Id).Single().LastUsedAt.Should().Be(_clock.GetUtcNow());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void should_refuse_a_key_without_a_name(string name)
    {
        var account = Service();

        new Action(() => _subject.Create(account.Id, name, null, null, "root")).Should().Throw<AccountRejectedException>().WithMessage("*name*");
    }

    [Fact]
    public void should_refuse_a_name_that_is_too_long_an_expiry_in_the_past_and_an_unknown_account()
    {
        var account = Service();

        new Action(() => _subject.Create(account.Id, new string('k', 101), null, null, "root")).Should().Throw<AccountRejectedException>();
        new Action(() => _subject.Create(account.Id, "k", null, _clock.GetUtcNow(), "root")).Should().Throw<AccountRejectedException>().WithMessage("*past*");
        new Action(() => _subject.Create(Guid.NewGuid(), "k", null, null, "root")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
        new Action(() => _subject.Revoke(Guid.NewGuid(), Guid.NewGuid(), "root")).Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
    }

    [Fact]
    public void should_remove_the_keys_with_their_account()
    {
        _accountService.Create("root", AccountKind.User, Roles.Admin, "correct horse battery", "test");
        var account = Service();
        _subject.Create(account.Id, "k", null, null, "root");

        _accountService.Delete(account.Id, "root");

        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM api_keys";
        command.ExecuteScalar().Should().Be(0L);
    }

    [Fact]
    public void should_not_store_the_same_secret_twice()
    {
        var account = Service();
        var created = _subject.Create(account.Id, "k", null, null, "root");

        var again = () => _keys.Add(created.Key with { Id = Guid.NewGuid() }, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(created.Secret))));

        again.Should().Throw<SqliteException>();
    }
}
