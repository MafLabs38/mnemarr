using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AuditLogFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteAuditLog _subject;

    public AuditLogFixture()
    {
        _subject = new SqliteAuditLog(_workspace.Database, _clock);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void should_return_the_newest_events_first_with_their_time()
    {
        _subject.Append("root", "a", "x", "one");
        _clock.Advance(TimeSpan.FromMinutes(1));
        _subject.Append("root", "b", "y");

        var events = _subject.Recent(10);

        events.Select(e => e.Action).Should().Equal("b", "a");
        events[0].At.Should().Be(new DateTimeOffset(2026, 11, 3, 9, 1, 0, TimeSpan.Zero));
        events[1].Detail.Should().Be("one");
    }

    [Fact]
    public void should_keep_to_one_action_and_to_the_limit_and_never_return_more_than_five_hundred()
    {
        foreach (var i in Enumerable.Range(0, 520))
        {
            _subject.Append("root", i % 2 == 0 ? "even" : "odd", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _subject.Recent(1000, "even").Should().HaveCount(260);
        _subject.Recent(2).Should().HaveCount(2);
        _subject.Recent(1000).Should().HaveCount(500);
    }

    [Fact]
    public void should_cut_a_text_that_is_too_long_instead_of_refusing_it()
    {
        _subject.Append(new string('a', 900), "x", "y", new string('d', 900));

        var stored = _subject.Recent(1).Single();
        stored.Actor.Should().HaveLength(500);
        stored.Detail.Should().HaveLength(500);
    }

    [Fact]
    public void should_forget_the_events_before_a_moment_and_say_how_many()
    {
        _subject.Append("root", "old", "x");
        _clock.Advance(TimeSpan.FromDays(100));
        _subject.Append("root", "recent", "y");

        _subject.Prune(_clock.GetUtcNow().AddDays(-90)).Should().Be(1);

        _subject.Recent(10).Select(e => e.Action).Should().Equal("recent");
    }
}
