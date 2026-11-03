using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Health;
using Mnemarr.Core.Test.Accounts;

namespace Mnemarr.Core.Test.Health;

public sealed class AccountsHealthCheckFixture : IDisposable
{
    private readonly AccountsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<HealthCheckResult> Check(AuthOptions? options = null) =>
        new AccountsHealthCheck(_h.Accounts, Options.Create(options ?? new AuthOptions())).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task should_be_healthy_when_somebody_can_sign_in()
    {
        _h.Admin();

        (await Check()).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_be_degraded_with_the_reason_when_there_is_no_account_at_all()
    {
        var result = await Check();

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["reason"].Should().Be("no-account");
        result.Description.Should().Contain("first administrator");
    }

    [Fact]
    public async Task should_be_degraded_with_the_reason_while_the_break_glass_reset_is_still_set()
    {
        _h.Admin();

        var result = await Check(new AuthOptions { ResetPasswordUser = "root", ResetPasswordFile = "/run/reset" });

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["reason"].Should().Be("break-glass-set");
        result.Description.Should().Contain("break-glass").And.NotContain("/run/reset");
    }

    [Fact]
    public async Task should_be_degraded_with_the_count_when_accounts_have_a_role_this_version_does_not_know()
    {
        _h.Admin();
        _h.Reader();
        using (var connection = _h.Workspace.Database.Open())
        {
            using var rename = connection.CreateCommand();
            rename.CommandText = "UPDATE accounts SET role = 'Retired' WHERE name = 'alice'";
            rename.ExecuteNonQuery();
        }

        var result = await Check();

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["reason"].Should().Be("unknown-role");
        result.Description.Should().StartWith("1 account(s)").And.NotContain("alice");
    }

    [Fact]
    public async Task should_be_unhealthy_when_the_accounts_cannot_be_read()
    {
        var gone = new SqliteAccountStore(new Mnemarr.Core.Storage.Database(Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = Path.Combine(_h.Workspace.Root, "gone") })));

        var result = await new AccountsHealthCheck(gone, Options.Create(new AuthOptions())).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
