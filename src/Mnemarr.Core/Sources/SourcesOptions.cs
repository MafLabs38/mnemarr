using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Sources;

/// <summary>Settings of the sources, from the <c>Sources</c> section.</summary>
public sealed class SourcesOptions
{
    /// <summary>
    /// The folders a source may be in. Empty means the <c>sources</c> folder of the data folder. A source outside them is refused:
    /// declaring one is how an API user asks Mnemarr to read a folder, so the operator decides where that can be.
    /// </summary>
    public string[] Roots { get; set; } = [];
}

public sealed class SourcesOptionsValidator : IValidateOptions<SourcesOptions>
{
    public ValidateOptionsResult Validate(string? name, SourcesOptions options) =>
        options.Roots.All(root => !string.IsNullOrWhiteSpace(root) && !root.Contains('\0') && Path.IsPathFullyQualified(root))
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Sources:Roots (MNEMARR__SOURCES__ROOTS__0, ...) must each be an absolute path: the operator says where a source may be, and a relative path would depend on where the process started.");
}
