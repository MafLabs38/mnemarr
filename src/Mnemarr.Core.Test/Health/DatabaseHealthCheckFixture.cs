using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Health;
using Mnemarr.Core.Storage;
using Mnemarr.Core.Test.Accounts;

namespace Mnemarr.Core.Test.Health;

public sealed class DatabaseHealthCheckFixture : IDisposable
{
    private readonly AccountsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static Task<HealthCheckResult> Check(IDatabase database) =>
        new DatabaseHealthCheck(database).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private void SetVersion(int version)
    {
        using var connection = _h.Workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version}";
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task should_be_healthy_when_the_database_opens_and_has_the_schema_of_this_version()
    {
        (await Check(_h.Workspace.Database)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_be_unhealthy_without_revealing_the_path_when_the_database_cannot_be_opened()
    {
        var folder = Path.Combine(_h.Workspace.Root, "gone");
        var database = new Database(Options.Create(new MnemarrOptions { DataFolder = folder }));

        var result = await Check(database);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("The database cannot be opened.").And.NotContain(folder);
    }

    [Fact]
    public async Task should_be_unhealthy_when_the_schema_changed_under_a_running_instance()
    {
        SetVersion(99);

        var result = await Check(_h.Workspace.Database);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("schema");
    }

    [Fact]
    public async Task should_be_unhealthy_and_say_so_when_the_database_was_written_by_a_newer_version()
    {
        SetVersion(Schema.Version + 1);

        var result = await Check(new Database(_h.Workspace.Options));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("The database was written by a newer version of Mnemarr.");
    }
}
