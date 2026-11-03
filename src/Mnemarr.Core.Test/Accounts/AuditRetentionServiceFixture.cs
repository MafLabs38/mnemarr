using Microsoft.Extensions.Logging.Abstractions;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AuditRetentionServiceFixture : IDisposable
{
    private readonly AccountsHarness _h = new(new AuthOptions { AuditRetentionDays = 90 });

    public void Dispose() => _h.Dispose();

    private AuditRetentionService Subject(AccountsHarness harness) => new(harness.Audit, harness.Options, harness.Clock, NullLogger<AuditRetentionService>.Instance);

    [Fact]
    public void should_forget_what_is_older_than_the_retention_and_keep_the_rest()
    {
        _h.Audit.Append("root", "old", "x");
        _h.Clock.Advance(TimeSpan.FromDays(91));
        _h.Audit.Append("root", "recent", "y");

        Subject(_h).Prune();

        _h.Audit.Recent(10).Select(e => e.Action).Should().Equal("recent");
    }

    [Fact]
    public async Task should_prune_at_start_and_stop_when_asked()
    {
        _h.Audit.Append("root", "old", "x");
        _h.Clock.Advance(TimeSpan.FromDays(100));
        var service = Subject(_h);
        var token = TestContext.Current.CancellationToken;

        await service.StartAsync(token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_h.Audit.Recent(10).Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, token);
        }

        await service.StopAsync(token);
        _h.Audit.Recent(10).Should().BeEmpty();
    }

    [Fact]
    public void should_not_fail_when_the_database_cannot_be_opened()
    {
        var gone = new SqliteAuditLog(
            new Mnemarr.Core.Storage.Database(Microsoft.Extensions.Options.Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = Path.Combine(_h.Workspace.Root, "gone") })),
            _h.Clock);
        var service = new AuditRetentionService(gone, _h.Options, _h.Clock, NullLogger<AuditRetentionService>.Instance);

        var prune = () => service.Prune();

        prune.Should().NotThrow();
    }
}
