namespace Mnemarr.Core.Indexing;

/// <summary>A fragment of a note, under the heading it belongs to: the unit that is indexed and searched.</summary>
public sealed record Chunk(string Heading, string Text);
