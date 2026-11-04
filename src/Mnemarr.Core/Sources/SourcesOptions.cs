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
