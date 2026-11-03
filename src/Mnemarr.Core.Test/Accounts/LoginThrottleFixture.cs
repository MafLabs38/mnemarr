using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class LoginThrottleFixture
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void should_lock_at_the_fifth_failure_and_not_before()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < 4; i++)
        {
            subject.Failed("alice");
            subject.IsLocked("alice").Should().BeFalse();
        }

        subject.Failed("alice");

        subject.IsLocked("alice").Should().BeTrue();
        subject.IsLocked("bob").Should().BeFalse();
    }

    [Fact]
    public void should_count_a_name_whatever_its_case()
    {
        var subject = new LoginThrottle(_clock);
        foreach (var name in new[] { "alice", "ALICE", "Alice", "aLiCe", "alice" })
        {
            subject.Failed(name);
        }

        subject.IsLocked("ALICE").Should().BeTrue();
    }

    [Fact]
    public void should_only_count_the_failures_of_the_last_fifteen_minutes()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < 4; i++)
        {
            subject.Failed("alice");
        }

        _clock.Advance(TimeSpan.FromMinutes(16));
        subject.Failed("alice");

        subject.IsLocked("alice").Should().BeFalse();
    }

    [Fact]
    public void should_forget_a_name_that_succeeds()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < 5; i++)
        {
            subject.Failed("alice");
        }

        subject.Succeeded("alice");

        subject.IsLocked("alice").Should().BeFalse();
    }

    [Fact]
    public void should_stay_bounded_when_many_names_fail_and_forget_the_old_ones()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < 10_000; i++)
        {
            subject.Failed($"name-{i}");
        }

        _clock.Advance(TimeSpan.FromMinutes(20));
        subject.Failed("fresh");
        for (var i = 0; i < 5; i++)
        {
            subject.Failed("fresh");
        }

        subject.IsLocked("fresh").Should().BeTrue();
        subject.IsLocked("name-1").Should().BeFalse();
    }
}
