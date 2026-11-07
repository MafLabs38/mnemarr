namespace Mnemarr.Core.Embeddings;

/// <summary>Whether the search by meaning can work: off (nobody asked), ready, or failed with a reason an operator can act on.</summary>
public enum EmbeddingState
{
    Off,
    /// <summary>Wanted, and the model is not loaded yet: the first pass loads it.</summary>
    Starting,
    Ready,
    Failed,
}

public sealed record EmbeddingStatus(EmbeddingState State, string? Reason = null);

/// <summary>
/// Turns text into vectors (D-26, ADR 0009). A query and a passage are told apart, because some models want a prefix for each: a provider applies its own,
/// so that a caller never changes when the model does. Vectors are normalised, so that the dot product is the cosine.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>The identifier stored with every vector: a change of model re-embeds in the background.</summary>
    string ModelId { get; }

    int Dimension { get; }

    /// <summary>Loads the model on the first call (verifying its files); the answer is kept.</summary>
    EmbeddingStatus Status();

    /// <summary>What <see cref="Status"/> answered, or null while the model has not been loaded: it never loads, so that a health probe never waits for the model.</summary>
    EmbeddingStatus? Known();

    float[] EmbedQuery(string text);

    IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts);
}
