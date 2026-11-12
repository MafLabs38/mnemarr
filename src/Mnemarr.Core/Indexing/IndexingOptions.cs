using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Indexing;

/// <summary>Settings of the indexing, from the <c>Indexing</c> section.</summary>
public sealed class IndexingOptions
{
    /// <summary>How often every source is scanned for changes, in seconds.</summary>
    public int ScanIntervalSeconds { get; set; } = 30;

    /// <summary>The largest note that is indexed and read, in megabytes (ADR 0043). A long conversation is one note and must stay searchable; the bound exists so that one file that is not a note cannot fill the memory.</summary>
    public int MaxNoteMegabytes { get; set; } = DefaultMaxNoteMegabytes;

    public const int DefaultMaxNoteMegabytes = 32;

    public const int HighestMaxNoteMegabytes = 128;

    /// <summary><see cref="MaxNoteMegabytes"/> in bytes.</summary>
    public long MaxNoteBytes => MaxNoteMegabytes * 1024L * 1024L;
}

public sealed class IndexingOptionsValidator : IValidateOptions<IndexingOptions>
{
    public ValidateOptionsResult Validate(string? name, IndexingOptions options)
    {
        if (options.ScanIntervalSeconds is < 1 or > 86_400)
        {
            return ValidateOptionsResult.Fail("Indexing:ScanIntervalSeconds (MNEMARR__INDEXING__SCANINTERVALSECONDS) must be between 1 and 86400.");
        }

        return options.MaxNoteMegabytes is >= 1 and <= IndexingOptions.HighestMaxNoteMegabytes
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Indexing:MaxNoteMegabytes (MNEMARR__INDEXING__MAXNOTEMEGABYTES) must be between 1 and {IndexingOptions.HighestMaxNoteMegabytes}.");
    }
}
