namespace Mnemarr.Core.Sources;

/// <summary>What a source holds. The kind decides how Mnemarr treats it (D-05, D-35).</summary>
public enum SourceKind
{
    /// <summary>Hand-written notes such as courses.</summary>
    Knowledge,

    /// <summary>The consolidated memory, written by Mnemarr.</summary>
    Memory,

    /// <summary>The documentation of an application, read from its repository.</summary>
    ApplicationDocs,
}

/// <summary>Whether Mnemarr may change what the source holds (D-35). A setting of each source.</summary>
public enum SourceMode
{
    ReadOnly,
    ReadWrite,
}

/// <summary>
/// A folder of Markdown notes that Mnemarr indexes. <paramref name="Managed"/> says that a configuration document owns it (configuration as code):
/// it is changed by changing the document, not by hand.
/// </summary>
public sealed record Source(Guid Id, string Name, SourceKind Kind, SourceMode Mode, string Path, DateTimeOffset CreatedAt, bool Managed = false);
