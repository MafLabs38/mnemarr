using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AdminAccountSeederFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteAccountStore _accounts;
    private readonly AccountService _service;

    public AdminAccountSeederFixture()
    {
        _accounts = new SqliteAccountStore(_workspace.Database);
        _service = new AccountService(_accounts, new SqliteAuditLog(_workspace.Database, _clock), new LoginThrottle(_clock), _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private AdminAccountSeeder Seeder(AuthOptions options) => new(_accounts, _service, Options.Create(options), NullLogger<AdminAccountSeeder>.Instance);

    private string PasswordFile(string content)
    {
        var path = Path.Combine(_workspace.Root, "admin-password");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void should_create_the_first_administrator_from_the_settings_and_the_password_file()
    {
        Seeder(new AuthOptions { AdminUser = "root", AdminPasswordFile = PasswordFile("a long enough password\n") }).Run();

        _accounts.List().Should().ContainSingle().Which.Should().Match<Account>(a => a.Name == "root" && a.Role == Roles.Admin && a.Kind == AccountKind.User);
        _service.SignIn("root", "a long enough password").Should().NotBeNull();
    }

    [Fact]
    public void should_do_nothing_once_there_is_an_account()
    {
        _service.Create("alice", AccountKind.User, Roles.Reader, "correct horse battery", "test");

        Seeder(new AuthOptions { AdminUser = "root", AdminPasswordFile = PasswordFile("a long enough password") }).Run();

        _accounts.List().Should().ContainSingle().Which.Name.Should().Be("alice");
    }

    [Fact]
    public void should_create_nothing_and_not_fail_when_no_administrator_is_configured()
    {
        Seeder(new AuthOptions()).Run();

        _accounts.Count().Should().Be(0);
    }

    [Fact]
    public void should_fail_to_start_on_a_password_that_is_too_weak()
    {
        var run = () => Seeder(new AuthOptions { AdminUser = "root", AdminPasswordFile = PasswordFile("short") }).Run();

        run.Should().Throw<AccountRejectedException>();
        _accounts.Count().Should().Be(0);
    }

    [Fact]
    public void should_leave_the_host_up_when_the_data_folder_does_not_exist_so_that_readiness_can_say_so()
    {
        var accounts = new SqliteAccountStore(new Mnemarr.Core.Storage.Database(Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = Path.Combine(_workspace.Root, "gone") })));
        var seeder = new AdminAccountSeeder(accounts, _service, Options.Create(new AuthOptions { AdminUser = "root", AdminPasswordFile = PasswordFile("a long enough password") }), NullLogger<AdminAccountSeeder>.Instance);

        var run = () => seeder.Run();

        run.Should().NotThrow();
    }

    [Fact]
    public void should_fail_to_start_when_the_password_file_is_missing()
    {
        var run = () => Seeder(new AuthOptions { AdminUser = "root", AdminPasswordFile = Path.Combine(_workspace.Root, "nope") }).Run();

        run.Should().Throw<FileNotFoundException>();
    }
}
