using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Health;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Test.Health;

public sealed class SourcesHealthCheckFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _workspace.Dispose();

    private SourceCatalog Catalog(IDatabase? database = null)
    {
        database ??= _workspace.Database;
        return new SourceCatalog(new SqliteSourceStore(database), new SqliteAuditLog(database, _clock), _workspace.Paths(), _clock);
    }

    private static Task<HealthCheckResult> Check(SourceCatalog catalog, ScanOutcomes? outcomes = null) =>
        new SourcesHealthCheck(catalog, outcomes ?? new ScanOutcomes()).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task should_be_degraded_with_the_kind_while_the_memory_vault_is_not_declared()
    {
        var result = await Check(Catalog());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["reason"].Should().Be("source-missing");
        result.Description.Should().Contain("Memory");
    }

    [Fact]
    public async Task should_be_healthy_once_the_base_is_there()
    {
        var catalog = Catalog();
        catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), SourceScope.All, "root");

        (await Check(catalog)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_be_degraded_with_the_name_of_a_source_whose_last_scan_failed_and_healthy_again_once_it_goes_well()
    {
        var catalog = Catalog();
        catalog.Add("memory", SourceKind.Memory, SourceMode.ReadOnly, _workspace.Folder("m"), SourceScope.All, "root");
        var courses = catalog.Add("courses", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("k"), SourceScope.All, "root");
        var outcomes = new ScanOutcomes();
        outcomes.Failed(courses.Id, _clock.GetUtcNow(), ScanOutcome.FolderMissing);

        var failing = await Check(catalog, outcomes);
        outcomes.Succeeded(courses.Id, _clock.GetUtcNow());
        var recovered = await Check(catalog, outcomes);

        failing.Status.Should().Be(HealthStatus.Degraded);
        failing.Data["reason"].Should().Be("source-unreadable");
        failing.Description.Should().Contain("courses").And.NotContain(courses.Path);
        recovered.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_be_unhealthy_when_the_sources_cannot_be_read()
    {
        var gone = new Database(Options.Create(new MnemarrOptions { DataFolder = Path.Combine(_workspace.Root, "gone") }));

        var result = await Check(Catalog(gone));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["reason"].Should().Be("sources-unreadable");
    }
}
