using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Indexing;

/// <summary>What is known of a source: how many notes and chunks, and when something was last indexed.</summary>
public sealed record IndexCounts(int Notes, int Chunks, DateTimeOffset? LastIndexedAt);

/// <summary>An indexed note: the hash of the content it was cut from, and the version of the chunker that cut it.</summary>
public sealed record NoteState(string ContentHash, int Chunker);

/// <summary>The notes of the sources, cut into chunks, with their full-text index.</summary>
public interface INoteIndex
{
    /// <summary>What is known of every note of a source, by path: what the next scan is compared with.</summary>
    IReadOnlyDictionary<string, NoteState> Notes(Guid sourceId);

    /// <summary>Adds a note, or replaces it with its new content and chunks, cut by the given version of the chunker.</summary>
    void Upsert(Guid sourceId, ScannedFile file, string title, string contentHash, int chunker, IReadOnlyList<Chunk> chunks, DateTimeOffset at);

    void Remove(Guid sourceId, string path);

    IndexCounts Counts(Guid sourceId);

    /// <summary>
    /// The chunks that match, best first, among the sources the caller may touch: the scope is a parameter, so that a search cannot be
    /// written without the question of what the caller may see (ADR 0016).
    /// </summary>
    IReadOnlyList<SearchHit> Search(SearchRequest request, SourceScope scope);
}
