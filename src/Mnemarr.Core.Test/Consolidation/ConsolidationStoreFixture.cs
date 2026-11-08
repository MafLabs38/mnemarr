using Mnemarr.Core.Consolidation;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Test.Consolidation;

public sealed class ConsolidationStoreFixture : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly TestWorkspace _workspace = new();
    private readonly SqliteConsolidationStore _store;
    private readonly SqliteConversationStore _conversations;

    public ConsolidationStoreFixture()
    {
        _store = new SqliteConsolidationStore(_workspace.Database);
        _conversations = new SqliteConversationStore(_workspace.Database);
    }

    public void Dispose() => _workspace.Dispose();

    private void Conversation(string session, long lines, DateTimeOffset updated) =>
        _conversations.Save(new ConversationRecord("pc", session, "app", "claude-code", lines, updated, updated, $"pc/app/{session}.md"));

    [Fact]
    public void should_find_what_was_saved_and_replace_it()
    {
        _store.Save(new ConsolidationRecord("pc", "s", 10, 4, 2, 0, null, Now));
        _store.Save(new ConsolidationRecord("pc", "s", 20, 8, 3, 1, "failed", Now));

        _store.Find("pc", "s").Should().Be(new ConsolidationRecord("pc", "s", 20, 8, 3, 1, "failed", Now));
        _store.Find("pc", "other").Should().BeNull();
    }

    [Fact]
    public void should_pick_the_conversations_that_were_left_alone_and_grew_the_oldest_first()
    {
        Conversation("old", 10, Now.AddHours(-3));
        Conversation("recent", 10, Now.AddMinutes(-5));
        Conversation("done", 10, Now.AddHours(-2));
        Conversation("grown", 30, Now.AddHours(-1));
        _store.Save(new ConsolidationRecord("pc", "done", 10, 5, 1, 0, null, Now));
        _store.Save(new ConsolidationRecord("pc", "grown", 10, 5, 1, 0, null, Now));

        var pending = _store.Pending(Now.AddMinutes(-30), maxTries: 3, limit: 10);

        pending.Select(item => item.Session).Should().Equal("old", "grown");
        _store.Pending(Now.AddMinutes(-30), 3, 1).Should().ContainSingle();
    }

    [Fact]
    public void should_leave_a_conversation_that_failed_too_often_until_it_grows_a_lot()
    {
        Conversation("bad", 10, Now.AddHours(-1));
        _store.Save(new ConsolidationRecord("pc", "bad", 0, 0, 0, 3, "failed", Now));

        _store.Pending(Now, maxTries: 3, limit: 10).Should().BeEmpty();

        Conversation("bad", 300, Now.AddHours(-1));
        _store.Pending(Now, maxTries: 3, limit: 10).Select(item => item.Session).Should().Equal("bad");
    }

    [Fact]
    public void should_count_what_waits_what_is_done_what_fails_and_the_runs_of_the_model_today()
    {
        Conversation("a", 10, Now.AddHours(-1));
        Conversation("b", 10, Now.AddHours(-1));
        Conversation("c", 10, Now.AddHours(-1));
        _store.Save(new ConsolidationRecord("pc", "a", 10, 4, 2, 0, null, Now));
        _store.Save(new ConsolidationRecord("pc", "b", 0, 0, 0, 3, "no login", Now.AddDays(-2)));
        _store.CountRun(Now.AddDays(-1));
        _store.CountRun(Now);
        _store.CountRun(Now.AddHours(1));

        var counts = _store.Counts(Now.Date, maxTries: 3);

        counts.Should().Be(new ConsolidationCounts(Waiting: 2, Done: 1, Failing: 1, Notes: 2, Today: 2));
        _store.LastFailure()!.Error.Should().Be("no login");
    }

    [Fact]
    public void should_give_the_tries_back_to_the_conversations_that_failed()
    {
        Conversation("bad", 10, Now.AddHours(-1));
        Conversation("fine", 10, Now.AddHours(-1));
        _store.Save(new ConsolidationRecord("pc", "bad", 0, 0, 0, 3, "failed", Now));
        _store.Save(new ConsolidationRecord("pc", "fine", 10, 5, 1, 0, null, Now));
        _store.Pending(Now, maxTries: 3, limit: 10).Select(item => item.Session).Should().BeEmpty();

        var retried = _store.ResetFailures(maxTries: 3);

        retried.Should().Be(1);
        _store.Pending(Now, maxTries: 3, limit: 10).Select(item => item.Session).Should().Equal("bad");
        _store.LastFailure().Should().BeNull();
    }
}
