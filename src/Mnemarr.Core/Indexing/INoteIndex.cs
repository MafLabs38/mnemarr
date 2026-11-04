using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Indexing;

/// <summary>What is known of a source: how many notes and chunks, and when something was last indexed.</summary>
public sealed record IndexCounts(int Notes, int Chunks, DateTimeOffset? LastIndexedAt);

/// <summary>
/// An indexed note: the hash of the content it was cut from, the version of the chunker that cut it, the version of the reading of its links and tags,
/// and the size and last write time of the file at that moment. A file whose size and time have not changed is not read again; the hash decides whether a file that was read changed.
/// </summary>
public sealed record NoteState(string ContentHash, int Chunker, int MetaVersion = 0, long Length = 0, long ModifiedTicks = 0);

/// <summary>A note as it is stored: its title, the hash of the content it was cut from, the version of the chunker that cut it, its chunks, and its links and tags.</summary>
public sealed record IndexedNote(string Title, string ContentHash, int Chunker, IReadOnlyList<Chunk> Chunks, NoteMeta Meta, int MetaVersion);

/// <summary>The notes of the sources, cut into chunks, with their full-text index.</summary>
public interface INoteIndex
{
    /// <summary>What is known of every note of a source, by path: what the next scan is compared with.</summary>
    IReadOnlyDictionary<string, NoteState> Notes(Guid sourceId);

    /// <summary>Adds a note, or replaces it with its new content, chunks and links, cut by the given version of the chunker.</summary>
    void Upsert(Guid sourceId, ScannedFile file, IndexedNote note, DateTimeOffset at);

    /// <summary>Records the size and last write time of a note whose content did not change, so that the next scan does not read it again.</summary>
    void Touch(Guid sourceId, ScannedFile file);

    /// <summary>Replaces the links and tags of a note whose text did not change but whose reading did (a new version of <see cref="MarkdownNotes"/>), without cutting it again.</summary>
    void UpdateMeta(Guid sourceId, ScannedFile file, NoteMeta meta, int metaVersion);

    void Remove(Guid sourceId, string path);

    /// <summary>The image files of a source that a note may show, by path: what the next scan is compared with.</summary>
    IReadOnlySet<string> Images(Guid sourceId);

    /// <summary>Makes the image files known for a source the given ones.</summary>
    void ReplaceImages(Guid sourceId, IReadOnlyCollection<string> paths);

    IndexCounts Counts(Guid sourceId);

    /// <summary>
    /// The chunks that match, best first, among the sources the caller may touch: the scope is a parameter, so that a search cannot be
    /// written without the question of what the caller may see (ADR 0016).
    /// </summary>
    IReadOnlyList<SearchHit> Search(SearchRequest request, SourceScope scope);
}
