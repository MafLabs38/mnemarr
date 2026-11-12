using System.Security.Cryptography;
using System.Text;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Embeddings;

/// <summary>A chunk to compute: its identifier and the text the model reads. Its <see cref="Hash"/> says whether the same text was computed before (ADR 0039).</summary>
public sealed record ChunkToEmbed(long ChunkId, string Text)
{
    /// <summary>The SHA-256 of the text the model reads, in hexadecimal: what a computed vector is kept under, so that the same text is never computed twice.</summary>
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}

/// <summary>A vector the model computed for a chunk, with the hash of the text it read.</summary>
public sealed record ComputedVector(long ChunkId, string Hash, float[] Vector);

/// <summary>A chunk near the question: smaller is nearer (the cosine distance).</summary>
public sealed record VectorMatch(long ChunkId, double Distance);

/// <summary>How much of the index is computed for a model: the chunks that exist, and the ones that have a vector.</summary>
public sealed record EmbeddingProgress(int Chunks, int Embedded);

/// <summary>
/// Keeps the vectors of the chunks and finds the nearest ones (D-25, ADR 0009). The store is keyed by (model, dimension): one table per model. A chunk has the vector
/// of one model: after a change of model, search by meaning covers the chunks that the new model has computed so far (the progress says how many), and the table of the
/// old model is dropped when nothing is left in it. The implementation is <c>sqlite-vec</c> inside the database.
/// </summary>
public interface IVectorStore
{
    /// <summary>Whether the vectors can be kept and searched; when they cannot, why (the library could not be loaded).</summary>
    bool TryPrepare(out string? reason);

    /// <summary>
    /// The chunks that have no vector of this model yet (or have one of another model), oldest first, but for the ones given as <paramref name="skipped"/>. A chunk whose text
    /// was computed before, in any note, is not returned: it is given the vector it already has (ADR 0039), so that the model only ever reads a text once.
    /// </summary>
    IReadOnlyList<ChunkToEmbed> Missing(string model, int dimension, int limit, IReadOnlySet<long> skipped);

    /// <summary>Keeps the vectors, and remembers them by the hash of their text. A chunk that was replaced meanwhile is skipped, not an error.</summary>
    void Save(string model, int dimension, IReadOnlyList<ComputedVector> items);

    /// <summary>
    /// The <paramref name="k"/> nearest chunks to a vector of the model, nearest first. Only chunks that are current for the model: this store owns the bookkeeping of which chunk has
    /// a vector of which model, and nothing else reads it.
    /// </summary>
    IReadOnlyList<VectorMatch> Nearest(string model, int dimension, float[] query, int k);

    /// <summary>How far the model is, among the chunks of the sources the caller may see (all of them when no scope is given).</summary>
    EmbeddingProgress Progress(string model, SourceScope? scope = null);

    /// <summary>Forgets the vectors of chunks that are gone, the tables of models nothing uses any more, and the computed vectors that have not been used for a while.</summary>
    void Tidy(string model, int dimension);
}
