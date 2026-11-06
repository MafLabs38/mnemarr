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
    public void should_never_track_more_names_than_the_cap_and_forget_the_oldest_names_that_are_not_accounts_first()
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
    public void should_not_give_back_the_attempts_of_an_account_to_whoever_floods_it_with_made_up_names()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            subject.TryReserve("admin", isAccount: true);
        }

        for (var i = 0; i < 2 * LoginThrottle.MaxTracked; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            subject.TryReserve($"made-up-{i}");
        }

        subject.TryReserve("admin", isAccount: true).Should().BeTrue("the fifth failure is still counted");
        subject.IsLocked("admin").Should().BeTrue();
    }

    [Fact]
    public void should_never_forget_a_lockout_of_an_account_to_make_room()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            subject.TryReserve("target", isAccount: true);
        }

        for (var i = 0; i < LoginThrottle.MaxTracked + 2_000; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            subject.TryReserve($"noise-{i}");
        }

        subject.IsLocked("target").Should().BeTrue();
        subject.TryReserve("target", isAccount: true).Should().BeFalse();
    }

    [Fact]
    public void should_make_room_for_a_new_name_by_forgetting_made_up_names_even_when_they_are_locked()
    {
        var subject = new LoginThrottle(_clock);
        for (var name = 0; name < LoginThrottle.MaxTracked; name++)
        {
            for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            {
                subject.TryReserve($"n{name}");
            }
        }

        subject.TryReserve("administrator", isAccount: true).Should().BeTrue("a real name always finds a place");
    }

    [Fact]
    public void should_refuse_a_new_name_when_every_tracked_name_is_an_account_and_not_scan_again_for_a_second()
    {
        var subject = new LoginThrottle(_clock);
        for (var name = 0; name < LoginThrottle.MaxTracked; name++)
        {
            subject.TryReserve($"account-{name}", isAccount: true);
        }

        subject.TryReserve("one").Should().BeFalse();
        subject.TryReserve("two").Should().BeFalse("the refusal is remembered for a second");
        _clock.Advance(TimeSpan.FromMinutes(16));

        subject.TryReserve("three").Should().BeTrue("the attempts have aged out");
    }

    [Fact]
    public void should_say_how_long_a_locked_name_stays_locked()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            subject.TryReserve("alice");
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        subject.LockedFor("alice").Should().Be(TimeSpan.FromMinutes(10), "the first of the five attempts was five minutes ago, in a window of fifteen");
        subject.LockedFor("nobody").Should().BeNull();
        subject.Succeeded("alice");
        subject.LockedFor("alice").Should().BeNull();
    }

    [Fact]
    public void should_not_extend_a_lockout_by_the_attempts_that_are_refused_while_it_lasts()
    {
        var subject = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            subject.TryReserve("alice");
        }

        for (var minute = 0; minute < 14; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            subject.TryReserve("alice").Should().BeFalse();
        }

        _clock.Advance(TimeSpan.FromMinutes(2));

        subject.TryReserve("alice").Should().BeTrue("fifteen minutes after the first attempts, however many were refused since");
    }

    [Fact]
    public void should_keep_the_count_of_a_name_that_turns_out_to_be_an_account()
    {
        var subject = new LoginThrottle(_clock);
        subject.TryReserve("later-an-account");
        subject.TryReserve("later-an-account", isAccount: true);

        for (var i = 0; i < 2 * LoginThrottle.MaxTracked; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            subject.TryReserve($"made-up-{i}");
        }

        for (var i = 0; i < LoginThrottle.MaxFailures - 2; i++)
        {
            subject.TryReserve("later-an-account", isAccount: true);
        }

        subject.IsLocked("later-an-account").Should().BeTrue("the two first attempts were not forgotten");
    }
}
