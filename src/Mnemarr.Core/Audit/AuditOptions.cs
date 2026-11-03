using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Audit;

/// <summary>Settings of the audit log, from the <c>Audit</c> section (see docs/configuration.md).</summary>
public sealed class AuditOptions
{
    public const int MaxRetentionDays = 3650;

    /// <summary>How long the audit log keeps its events, in days.</summary>
    public int RetentionDays { get; set; } = 90;
}

public sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options) =>
        options.RetentionDays is < 1 or > AuditOptions.MaxRetentionDays
            ? ValidateOptionsResult.Fail($"Audit:RetentionDays (MNEMARR__AUDIT__RETENTIONDAYS) must be between 1 and {AuditOptions.MaxRetentionDays}.")
            : ValidateOptionsResult.Success;
}
