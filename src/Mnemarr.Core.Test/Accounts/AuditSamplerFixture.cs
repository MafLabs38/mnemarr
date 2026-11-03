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

        subject.Allow("a").Should().BeTrue();
        subject.Allow("a").Should().BeFalse();
        subject.Allow("b").Should().BeTrue();
        _clock.Advance(TimeSpan.FromMinutes(1));

        subject.Allow("a").Should().BeTrue();
    }

    [Fact]
    public void should_stop_at_a_ceiling_a_minute_in_all_whatever_the_keys()
    {
        var subject = new AuditSampler(_clock);

        Enumerable.Range(0, AuditSampler.MaxPerMinute + 25).Count(i => subject.Allow($"key-{i}")).Should().Be(AuditSampler.MaxPerMinute);
        _clock.Advance(TimeSpan.FromMinutes(1));

        subject.Allow("fresh").Should().BeTrue();
    }
}
