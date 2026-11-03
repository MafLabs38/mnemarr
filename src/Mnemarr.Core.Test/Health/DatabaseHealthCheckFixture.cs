using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Health;
using Mnemarr.Core.Storage;
using Mnemarr.Core.Test.Accounts;

namespace Mnemarr.Core.Test.Health;

public sealed class DatabaseHealthCheckFixture : IDisposable
{
    private readonly AccountsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static Task<HealthCheckResult> Check(DatabaseHealthCheck subject) =>
        subject.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task should_be_healthy_when_the_database_opens_and_somebody_can_sign_in()
    {
        _h.Admin();

        (await Check(new DatabaseHealthCheck(_h.Workspace.Database, _h.Accounts))).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_be_degraded_with_the_reason_when_there_is_no_account_at_all()
    {
        var result = await Check(new DatabaseHealthCheck(_h.Workspace.Database, _h.Accounts));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("first administrator");
    }

    [Fact]
    public async Task should_be_unhealthy_without_revealing_the_path_when_the_database_cannot_be_opened()
    {
        var folder = Path.Combine(_h.Workspace.Root, "gone");
        var database = new Database(Options.Create(new MnemarrOptions { DataFolder = folder }));

        var result = await Check(new DatabaseHealthCheck(database, new SqliteAccountStore(database)));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("The database cannot be opened.").And.NotContain(folder);
    }

    [Fact]
    public async Task should_be_unhealthy_when_the_schema_is_not_the_one_this_version_expects()
    {
        using (var connection = _h.Workspace.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        var result = await Check(new DatabaseHealthCheck(_h.Workspace.Database, _h.Accounts));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("schema");
    }
}
