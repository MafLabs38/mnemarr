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

    /// <summary>The conversations of the assistants, sent by the collectors and written by Mnemarr as notes (ADR 0024).</summary>
    Conversations,
}

/// <summary>Whether Mnemarr may change what the source holds (D-35). A setting of each source.</summary>
public enum SourceMode
{
    ReadOnly,
    ReadWrite,
}

/// <summary>
/// A folder of Markdown notes that Mnemarr indexes, or the checkout of a Git repository (<paramref name="Repository"/>). <paramref name="Managed"/> says that something other than a
/// person owns it, and it is not changed by hand: a configuration document (configuration as code: change the document), or Mnemarr itself (the conversations it collects).
/// </summary>
public sealed record Source(Guid Id, string Name, SourceKind Kind, SourceMode Mode, string Path, DateTimeOffset CreatedAt, bool Managed = false, GitRepository? Repository = null);

/// <summary>
/// The Git repository a source is the checkout of (ADR 0022): an HTTPS address with no credentials in it, a branch (the default branch of the
/// repository when null), and the name of the operator's credential to use (none for a public repository). The folder of the source is where it is cloned.
/// </summary>
public sealed record GitRepository(string Url, string? Branch = null, string? Credential = null);
