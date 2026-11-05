namespace Mnemarr.Core.Collectors;

/// <summary>
/// The state of a machine, told from facts alone (ADR 0027 §7): the same answer for the page and for the health check. A function of the
/// machine and the clock, so that it is tried without waiting.
/// </summary>
public static class MachineStates
{
    public static MachineState Of(Machine machine, DateTimeOffset now, CollectorOptions options)
    {
        if (machine.RevokedAt is not null)
        {
            return MachineState.Refused;
        }

        if (machine.LastSeenAt is not { } seen)
        {
            return MachineState.Waiting;
        }

        if (!string.IsNullOrEmpty(machine.Error))
        {
            return MachineState.Failing;
        }

        if (now - seen <= options.OnlineWithin)
        {
            return MachineState.Online;
        }

        return machine.Kind == MachineKind.AlwaysOn ? MachineState.Silent : MachineState.Away;
    }

    /// <summary>Whether this is an alert: an always-on machine silent for longer than the setting, or reporting an error. An intermittent machine is never one.</summary>
    public static bool IsAlert(Machine machine, DateTimeOffset now, CollectorOptions options) =>
        machine.Kind == MachineKind.AlwaysOn && machine.RevokedAt is null && (
            !string.IsNullOrEmpty(machine.Error) ||
            (machine.LastSeenAt is { } seen && now - seen > options.SilenceAfter));
}
