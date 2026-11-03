using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AuditSamplerFixture
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void should_let_a_key_through_once_a_minute()
    {
        var subject = new AuditSampler(_clock);

        subject.Allow("x", "a").Should().BeTrue();
        subject.Allow("x", "a").Should().BeFalse();
        subject.Allow("x", "b").Should().BeTrue();
        _clock.Advance(TimeSpan.FromMinutes(1));

        subject.Allow("x", "a").Should().BeTrue();
    }

    [Fact]
    public void should_stop_at_a_ceiling_a_minute_in_all_whatever_the_keys()
    {
        var subject = new AuditSampler(_clock);

        Enumerable.Range(0, AuditSampler.MaxPerMinute + 25).Count(i => subject.Allow("x", $"key-{i}")).Should().Be(AuditSampler.MaxPerMinute);
        _clock.Advance(TimeSpan.FromMinutes(1));

        subject.Allow("x", "fresh").Should().BeTrue();
    }

    [Fact]
    public void should_give_each_kind_of_event_its_own_ceiling_so_that_a_flood_of_one_cannot_hide_another()
    {
        var subject = new AuditSampler(_clock);
        Enumerable.Range(0, AuditSampler.MaxPerMinute + 25).Count(i => subject.Allow("key.refused", $"prefix-{i}")).Should().Be(AuditSampler.MaxPerMinute);

        subject.Allow("login.locked", "alice").Should().BeTrue();
    }

    [Fact]
    public void should_not_grow_with_kinds_of_event_that_nobody_planned()
    {
        var subject = new AuditSampler(_clock);

        Enumerable.Range(0, AuditSampler.MaxKinds + 10).Count(i => subject.Allow($"kind-{i}", "k")).Should().Be(AuditSampler.MaxKinds);
    }
}
