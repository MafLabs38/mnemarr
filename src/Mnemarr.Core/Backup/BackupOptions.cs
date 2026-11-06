using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Backup;

/// <summary>
/// Settings of the snapshots of the database, from the <c>Backup</c> section (ADR 0029). Mnemarr keeps a consistent copy of its database in a folder
/// of the data folder; what copies the data folder away (a backup tool of the host) is the operator's, as ADR 0010 decided.
/// </summary>
public sealed class BackupOptions
{
    /// <summary>Whether Mnemarr keeps snapshots of its database. On by default: a copy of a SQLite file that is in use is not always whole.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where the snapshots are kept; the <c>backup</c> folder of the data folder when it is not set.</summary>
    public string? Folder { get; set; }

    /// <summary>A snapshot is taken when the last one is this many hours old.</summary>
    public int EveryHours { get; set; } = 24;

    /// <summary>How many snapshots are kept; older ones are deleted.</summary>
    public int Keep { get; set; } = 7;
}

public sealed class BackupOptionsValidator : IValidateOptions<BackupOptions>
{
    public ValidateOptionsResult Validate(string? name, BackupOptions options) =>
        options.EveryHours is < 1 or > 168 || options.Keep is < 1 or > 60
            ? ValidateOptionsResult.Fail("Backup: EveryHours is 1 to 168 and Keep is 1 to 60 (MNEMARR__BACKUP__EVERYHOURS, MNEMARR__BACKUP__KEEP).")
            : ValidateOptionsResult.Success;
}
