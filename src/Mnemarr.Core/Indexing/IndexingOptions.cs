using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Indexing;

/// <summary>Settings of the indexing, from the <c>Indexing</c> section.</summary>
public sealed class IndexingOptions
{
    /// <summary>How often every source is scanned for changes, in seconds.</summary>
    public int ScanIntervalSeconds { get; set; } = 30;
}

public sealed class IndexingOptionsValidator : IValidateOptions<IndexingOptions>
{
    public ValidateOptionsResult Validate(string? name, IndexingOptions options) =>
        options.ScanIntervalSeconds is >= 1 and <= 86_400
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Indexing:ScanIntervalSeconds (MNEMARR__INDEXING__SCANINTERVALSECONDS) must be between 1 and 86400.");
}
