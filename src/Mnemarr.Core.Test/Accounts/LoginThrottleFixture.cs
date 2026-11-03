using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class LoginThrottleFixture
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void should_count_an_attempt_when_it_is_reserved_and_refuse_the_sixth()
    {
        var subject = new LoginThrottle(_clock);

        Enumerable.Range(0, LoginThrottle.MaxFailures).Should().OnlyContain(_ => subject.TryReserve("alice"));

        subject.IsLocked("alice").Should().BeTrue();
        subject.TryReserve("alice").Should().BeFalse();
        subject.IsLocked("bob").Should().BeFalse();
    }

    [Fact]
    public async Task should_let_no_more_than_five_of_a_burst_of_parallel_attempts_through()
    {
        var subject = new LoginThrottle(_clock);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => subject.TryReserve("admin"), TestContext.Current.CancellationToken)));

        results.Count(allowed => allowed).Should().Be(LoginThrottle.MaxFailures);
    }

    [Fact]
    public void should_count_a_name_whatever_its_case()
    {
        var subject = new LoginThrottle(_clock);
        foreach (var name in new[] { "alice", "ALICE", "Alice", "aLiCe", "alice" })
        {
            subject.TryReserve(name);
        }

        subject.IsLocked("ALICE").Should().BeTrue();
    }

    [Fact]
    public void should_only_count_the_attempts_of_the_last_fifteen_minutes()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            subject.TryReserve("alice");
        }

        _clock.Advance(TimeSpan.FromMinutes(16));

        subject.TryReserve("alice").Should().BeTrue();
        subject.IsLocked("alice").Should().BeFalse();
    }

    [Fact]
    public void should_forget_a_name_that_succeeds()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            subject.TryReserve("alice");
        }

        subject.Succeeded("alice");

        subject.IsLocked("alice").Should().BeFalse();
    }

    [Fact]
    public void should_never_track_more_names_than_the_cap_and_forget_the_oldest_first()
    {
        var subject = new LoginThrottle(_clock);
        subject.TryReserve("first");
        for (var i = 0; i < LoginThrottle.MaxTracked + 500; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            subject.TryReserve($"name-{i}");
        }

        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            subject.TryReserve("last");
        }

        subject.IsLocked("last").Should().BeTrue();
        // "first" was the oldest: it was forgotten to make room, so it starts again from nothing.
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            subject.TryReserve("first").Should().BeTrue();
        }
    }

    [Fact]
    public void should_never_forget_a_name_that_is_locked_to_make_room()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            subject.TryReserve("target");
        }

        for (var i = 0; i < LoginThrottle.MaxTracked + 2_000; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            subject.TryReserve($"noise-{i}");
        }

        subject.IsLocked("target").Should().BeTrue();
        subject.TryReserve("target").Should().BeFalse();
    }

    [Fact]
    public void should_refuse_a_new_name_rather_than_forget_a_lockout_when_every_tracked_name_is_locked()
    {
        var subject = new LoginThrottle(_clock);
        for (var name = 0; name < LoginThrottle.MaxTracked; name++)
        {
            for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            {
                subject.TryReserve($"n{name}");
            }
        }

        subject.TryReserve("newcomer").Should().BeFalse();
        subject.IsLocked("n0").Should().BeTrue();
    }
}
