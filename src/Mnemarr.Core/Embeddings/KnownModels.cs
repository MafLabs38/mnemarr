using System.Diagnostics.CodeAnalysis;

namespace Mnemarr.Core.Embeddings;

/// <summary>A model Mnemarr can compute embeddings with: the size of its vectors and the files it is made of, each with the SHA-256 it must have.</summary>
public sealed record KnownModel(string Id, int Dimension, string Revision, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// The models that are supported (ADR 0012, ADR 0034). The model is pinned like the vector library: a fixed upstream revision and the SHA-256 of each file,
/// verified before the model is loaded, wherever the files come from. A file that differs is not loaded.
/// </summary>
public static class KnownModels
{
    public const string Default = "granite-embedding-97m-multilingual-r2";

    private static readonly Dictionary<string, KnownModel> Models = new(StringComparer.Ordinal)
    {
        [Default] = new KnownModel(
            Default,
            384,
            "835ad14087e140460703cf0fae09f97d469d65c2",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model.onnx"] = "68e592b160673d30250824c1116bc6ab33f70efb22b97c9e1d7ce1e69c1c9d70",
                ["tokenizer.json"] = "4f2842d568e2724370aec203652a42ac783c7937f8347a1a2cc7506d71f1582f",
            }),
    };

    public static IEnumerable<string> Identifiers => Models.Keys;

    public static bool TryGet(string? id, [NotNullWhen(true)] out KnownModel? model)
    {
        model = null;
        return id is not null && Models.TryGetValue(id, out model);
    }
}
