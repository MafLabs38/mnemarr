using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Test.Accounts;

namespace Mnemarr.Core.Test.Audit;

public sealed class AuditRetentionServiceFixture : IDisposable
{
    private readonly AccountsHarness _h = new();
    private readonly IOptions<AuditOptions> _options = Options.Create(new AuditOptions { RetentionDays = 90 });

    public void Dispose() => _h.Dispose();

    private AuditRetentionService Subject(AccountsHarness harness) => new(harness.Audit, _options, harness.Clock, NullLogger<AuditRetentionService>.Instance);

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
    public async Task should_prune_at_start_without_holding_the_start_back_and_again_every_period()
    {
        var pruned = new SemaphoreSlim(0);
        var audit = new CountingAuditLog(pruned);
        var service = new AuditRetentionService(audit, _options, _h.Clock, NullLogger<AuditRetentionService>.Instance);
        var token = TestContext.Current.CancellationToken;

        // The service makes its timer before its first pass, so once the first pass is signalled the fake clock can be moved: keep it so.
        await service.StartAsync(token);
        (await pruned.WaitAsync(TimeSpan.FromSeconds(30), token)).Should().BeTrue("the first pass runs at start");
        _h.Clock.Advance(TimeSpan.FromHours(7));
        (await pruned.WaitAsync(TimeSpan.FromSeconds(30), token)).Should().BeTrue("and again once the period has passed");

        await service.StopAsync(token);
    }

    [Fact]
    public void should_not_fail_when_the_database_cannot_be_opened()
    {
        var gone = new SqliteAuditLog(
            new Mnemarr.Core.Storage.Database(Microsoft.Extensions.Options.Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = Path.Combine(_h.Workspace.Root, "gone") })),
            _h.Clock);
        var service = new AuditRetentionService(gone, _options, _h.Clock, NullLogger<AuditRetentionService>.Instance);

        var prune = () => service.Prune();

        prune.Should().NotThrow();
    }

    /// <summary>An audit log that only says when it was pruned.</summary>
    private sealed class CountingAuditLog : IAuditLog
    {
        private readonly SemaphoreSlim _pruned;

        public CountingAuditLog(SemaphoreSlim pruned)
        {
            _pruned = pruned;
        }

        public void Append(string actor, string action, string target, string detail = "")
        {
        }

        public int Prune(DateTimeOffset before)
        {
            _pruned.Release();
            return 0;
        }

        public IReadOnlyList<AuditEvent> Recent(int limit, string? action = null, long? before = null) => [];
    }
}
