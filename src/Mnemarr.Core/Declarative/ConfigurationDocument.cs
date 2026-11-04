namespace Mnemarr.Core.Declarative;

/// <summary>
/// A configuration document (configuration as code, ADR 0020): the state that is wanted, in the shape of the rules behind it. A section that
/// is absent is not managed by the document and is left as it is; a section that is present is, even when it is empty.
/// </summary>
public sealed class ConfigurationDocument
{
    /// <summary>The memory vault: one object, because there is exactly one.</summary>
    public SourceEntry? Memory { get; set; }

    public List<SourceEntry>? Knowledge { get; set; }

    public List<SourceEntry>? ApplicationDocs { get; set; }
}

/// <summary>A source as the document says it. The name of the memory vault is optional (it is called <c>memory</c>); the mode is the default of its kind when it is not given.</summary>
public sealed class SourceEntry
{
    public string? Name { get; set; }

    public string? Path { get; set; }

    public string? Mode { get; set; }
}
