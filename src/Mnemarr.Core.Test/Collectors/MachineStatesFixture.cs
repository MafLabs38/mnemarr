using Mnemarr.Core.Collectors;

namespace Mnemarr.Core.Test.Collectors;

public sealed class MachineStatesFixture
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly CollectorOptions Options = new() { OnlineWithinMinutes = 5, SilenceAfterMinutes = 30 };

    private static Machine Machine(MachineKind kind, int? seenMinutesAgo = null, string? error = null, bool revoked = false, bool paused = false) => new(
        "pc", kind, Guid.NewGuid(), Now.AddDays(-1), seenMinutesAgo is { } minutes ? Now.AddMinutes(-minutes) : null, "0.1.0", 0, error, revoked ? Now.AddHours(-1) : null, paused);

    [Theory]
    [InlineData(MachineKind.Intermittent, null, MachineState.Waiting)]
    [InlineData(MachineKind.AlwaysOn, null, MachineState.Waiting)]
    [InlineData(MachineKind.Intermittent, 1, MachineState.Online)]
    [InlineData(MachineKind.AlwaysOn, 5, MachineState.Online)]
    [InlineData(MachineKind.Intermittent, 6, MachineState.Away)]
    [InlineData(MachineKind.Intermittent, 5000, MachineState.Away)]
    [InlineData(MachineKind.AlwaysOn, 6, MachineState.Silent)]
    [InlineData(MachineKind.AlwaysOn, 5000, MachineState.Silent)]
    public void should_tell_the_state_of_a_machine_from_its_kind_and_when_it_was_last_heard_from(MachineKind kind, int? seenMinutesAgo, MachineState expected) =>
        MachineStates.Of(Machine(kind, seenMinutesAgo), Now, Options).Should().Be(expected);

    [Fact]
    public void should_say_refused_for_a_revoked_machine_and_failing_for_one_that_reports_an_error_before_anything_else()
    {
        MachineStates.Of(Machine(MachineKind.AlwaysOn, 1, revoked: true), Now, Options).Should().Be(MachineState.Refused);
        MachineStates.Of(Machine(MachineKind.Intermittent, 1, error: "a reader failed"), Now, Options).Should().Be(MachineState.Failing);
        MachineStates.Of(Machine(MachineKind.Intermittent, error: "a reader failed", revoked: true), Now, Options).Should().Be(MachineState.Refused);
    }

    [Theory]
    [InlineData(MachineKind.AlwaysOn, 30, false)]
    [InlineData(MachineKind.AlwaysOn, 31, true)]
    [InlineData(MachineKind.Intermittent, 100000, false)]
    public void should_make_an_alert_only_of_an_always_on_machine_silent_for_longer_than_the_setting(MachineKind kind, int seenMinutesAgo, bool alert) =>
        MachineStates.IsAlert(Machine(kind, seenMinutesAgo), Now, Options).Should().Be(alert);

    [Fact]
    public void should_make_an_alert_of_an_always_on_machine_that_fails_and_never_of_a_revoked_one_or_a_sleeping_one()
    {
        MachineStates.IsAlert(Machine(MachineKind.AlwaysOn, 1, error: "x"), Now, Options).Should().BeTrue();
        MachineStates.IsAlert(Machine(MachineKind.Intermittent, 1, error: "x"), Now, Options).Should().BeFalse();
        MachineStates.IsAlert(Machine(MachineKind.AlwaysOn, 1000, revoked: true), Now, Options).Should().BeFalse();
        MachineStates.IsAlert(Machine(MachineKind.AlwaysOn), Now, Options).Should().BeFalse("a machine that never spoke is waiting for its first contact, not silent");
    }

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(5, 4, false)]
    [InlineData(5, 30, true)]
    [InlineData(5, 999999, false)]
    public void should_accept_the_two_delays_only_when_the_second_is_not_shorter_than_the_first(int online, int silence, bool valid) =>
        new CollectorOptionsValidator().Validate(null, new CollectorOptions { OnlineWithinMinutes = online, SilenceAfterMinutes = silence }).Succeeded.Should().Be(valid);

    [Fact]
    public void should_say_paused_for_a_machine_that_says_a_person_paused_it_and_is_still_heard_from_and_never_make_an_alert_of_it()
    {
        MachineStates.Of(Machine(MachineKind.AlwaysOn, 1, paused: true), Now, Options).Should().Be(MachineState.Paused);
        MachineStates.Of(Machine(MachineKind.Intermittent, 1, paused: true), Now, Options).Should().Be(MachineState.Paused);
        MachineStates.IsAlert(Machine(MachineKind.AlwaysOn, 1, paused: true), Now, Options).Should().BeFalse("a pause is deliberate");
    }

    [Fact]
    public void should_not_keep_a_machine_paused_that_stopped_saying_so_and_still_say_failing_or_refused_first()
    {
        MachineStates.Of(Machine(MachineKind.Intermittent, 60, paused: true), Now, Options).Should().Be(MachineState.Away, "it is off, whatever it last said");
        MachineStates.Of(Machine(MachineKind.AlwaysOn, 60, paused: true), Now, Options).Should().Be(MachineState.Silent);
        MachineStates.Of(Machine(MachineKind.Intermittent, 1, error: "x", paused: true), Now, Options).Should().Be(MachineState.Failing);
        MachineStates.Of(Machine(MachineKind.Intermittent, 1, revoked: true, paused: true), Now, Options).Should().Be(MachineState.Refused);
    }
}
