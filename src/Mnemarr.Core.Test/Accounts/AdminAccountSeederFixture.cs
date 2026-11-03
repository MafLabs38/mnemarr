using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AdminAccountSeederFixture : IDisposable
{
    private readonly AccountsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private AdminAccountSeeder Seeder(AuthOptions options) => new(_h.Accounts, _h.Service, Options.Create(options), NullLogger<AdminAccountSeeder>.Instance);

    private string PasswordFile(string content, string name = "admin-password")
    {
        var path = Path.Combine(_h.Workspace.Root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("a long enough password")]
    [InlineData("a long enough password\n")]
    [InlineData("a long enough password\r\n")]
    [InlineData("\uFEFFa long enough password\n")]
    public void should_create_the_first_administrator_from_the_settings_and_the_password_file(string content)
    {
        Seeder(new AuthOptions { AdminUser = "root", AdminPasswordFile = PasswordFile(content) }).Run();

        _h.Accounts.List().Should().ContainSingle().Which.Should().Match<Account>(a => a.Name == "root" && a.Role == Roles.Admin && a.Kind == AccountKind.User);
        _h.Service.SignIn("root", "a long enough password").Should().NotBeNull();
    }

    [Fact]
    public void should_do_nothing_once_there_is_an_account()
    {
        _h.Reader();

        Seeder(new AuthOptions { AdminUser = "root", AdminPasswordFile = PasswordFile("a long enough password") }).Run();

        _h.Accounts.List().Should().ContainSingle().Which.Name.Should().Be("alice");
    }

    [Fact]
    public void should_create_nothing_and_not_fail_when_no_administrator_is_configured()
    {
        Seeder(new AuthOptions()).Run();

        _h.Accounts.Count().Should().Be(0);
    }

    [Theory]
    [InlineData("short", "refused")]
    [InlineData("", "refused")]
    public void should_stop_the_start_with_a_message_when_the_password_is_not_acceptable(string content, string reason)
    {
        var run = () => Seeder(new AuthOptions { AdminUser = "root", AdminPasswordFile = PasswordFile(content) }).Run();

        run.Should().Throw<InvalidOperationException>().WithMessage($"*first administrator was {reason}*");
        _h.Accounts.Count().Should().Be(0);
    }

    [Fact]
    public void should_stop_the_start_with_a_message_when_the_password_file_cannot_be_read()
    {
        var missing = Path.Combine(_h.Workspace.Root, "nope");

        var run = () => Seeder(new AuthOptions { AdminUser = "root", AdminPasswordFile = missing }).Run();

        run.Should().Throw<InvalidOperationException>().WithMessage($"*cannot be read: {missing}");
    }

    [Fact]
    public void should_leave_the_host_up_when_the_data_folder_does_not_exist_so_that_readiness_can_say_so()
    {
        var accounts = new SqliteAccountStore(new Mnemarr.Core.Storage.Database(Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = Path.Combine(_h.Workspace.Root, "gone") })));
        var seeder = new AdminAccountSeeder(accounts, _h.Service, Options.Create(new AuthOptions { AdminUser = "root", AdminPasswordFile = PasswordFile("a long enough password") }), NullLogger<AdminAccountSeeder>.Instance);

        var run = () => seeder.Run();

        run.Should().NotThrow();
    }

    [Fact]
    public void should_reset_the_password_of_the_named_user_at_start_even_when_accounts_exist()
    {
        var alice = _h.Reader();
        _h.Service.SetEnabled(alice.Id, false, "x");
        _h.Admin();

        Seeder(new AuthOptions { ResetPasswordUser = "alice", ResetPasswordFile = PasswordFile("a brand new password", "reset") }).Run();

        _h.Service.SignIn("alice", "a brand new password").Should().NotBeNull();
        _h.Audit.Recent(10, "account.reset").Should().ContainSingle();
    }

    [Theory]
    [InlineData("nobody", "a brand new password")]
    [InlineData("alice", "short")]
    public void should_stop_the_start_when_the_reset_is_refused(string user, string password)
    {
        _h.Reader();

        var run = () => Seeder(new AuthOptions { ResetPasswordUser = user, ResetPasswordFile = PasswordFile(password, "reset") }).Run();

        run.Should().Throw<InvalidOperationException>().WithMessage($"*reset of '{user}' was refused*");
    }

    [Fact]
    public void should_stop_the_start_when_the_reset_file_cannot_be_read()
    {
        var run = () => Seeder(new AuthOptions { ResetPasswordUser = "alice", ResetPasswordFile = Path.Combine(_h.Workspace.Root, "nope") }).Run();

        run.Should().Throw<InvalidOperationException>().WithMessage("*of the reset cannot be read*");
    }
}
