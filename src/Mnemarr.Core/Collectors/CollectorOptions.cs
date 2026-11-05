using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Collectors;

/// <summary>Settings of the machines, from the <c>Collectors</c> section (ADR 0027 §7).</summary>
public sealed class CollectorOptions
{
    public const int MaxMinutes = 7 * 24 * 60;

    /// <summary>A machine heard from within this many minutes is online.</summary>
    public int OnlineWithinMinutes { get; set; } = 5;

    /// <summary>An always-on machine not heard from for this many minutes is an alert.</summary>
    public int SilenceAfterMinutes { get; set; } = 30;

    public TimeSpan OnlineWithin => TimeSpan.FromMinutes(OnlineWithinMinutes);

    public TimeSpan SilenceAfter => TimeSpan.FromMinutes(SilenceAfterMinutes);
}

public sealed class CollectorOptionsValidator : IValidateOptions<CollectorOptions>
{
    public ValidateOptionsResult Validate(string? name, CollectorOptions options)
    {
        if (options.OnlineWithinMinutes is < 1 or > CollectorOptions.MaxMinutes)
        {
            return ValidateOptionsResult.Fail($"Collectors:OnlineWithinMinutes (MNEMARR__COLLECTORS__ONLINEWITHINMINUTES) must be between 1 and {CollectorOptions.MaxMinutes}.");
        }

        if (options.SilenceAfterMinutes < options.OnlineWithinMinutes || options.SilenceAfterMinutes > CollectorOptions.MaxMinutes)
        {
            return ValidateOptionsResult.Fail($"Collectors:SilenceAfterMinutes (MNEMARR__COLLECTORS__SILENCEAFTERMINUTES) must be at least OnlineWithinMinutes and at most {CollectorOptions.MaxMinutes}.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>Settings of the ingest, from the <c>Ingest</c> section.</summary>
public sealed class IngestOptions
{
    /// <summary>
    /// Whether a key may send only for the machine it was given to. On by default; off for a sandbox, or the day before every machine is enrolled,
    /// when a hand-made <c>Collector</c> account is still allowed to write under any machine name.
    /// </summary>
    public bool RequireEnrolment { get; set; } = true;
}
