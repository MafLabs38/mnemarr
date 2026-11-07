using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Embeddings;

/// <summary>
/// Settings of the search by meaning (ADR 0034, from the <c>Embeddings</c> section). Without a <see cref="ModelFolder"/> the feature is off: search stays
/// by keywords and nothing is computed.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>The model, by identifier. Only the listed models are supported (<see cref="KnownModels"/>).</summary>
    public string Model { get; set; } = KnownModels.Default;

    /// <summary>The folder that holds the files of the model (<c>model.onnx</c>, <c>tokenizer.json</c>). Empty: the search by meaning is off.</summary>
    public string? ModelFolder { get; set; }

    /// <summary>The loadable <c>sqlite-vec</c> library, at a path Mnemarr is given by its operator or its image, never by input.</summary>
    public string? ExtensionPath { get; set; }

    /// <summary>The CPU threads the model may use. The first run over a long history is slow; the rest of the host must stay usable.</summary>
    public int Threads { get; set; } = 2;

    /// <summary>The longest a passage may be, in tokens: the rest is not read by the model.</summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>How many passages are computed together.</summary>
    public int BatchSize { get; set; } = 8;

    /// <summary>How long the indexer waits, in seconds, when there is nothing to compute, before it looks again.</summary>
    public int IdleSeconds { get; set; } = 15;

    /// <summary>
    /// The cosine distance beyond which a passage is not a match by meaning (0 is the same, 2 is opposite): the nearest passages of a question that nothing answers
    /// are not shown as answers. The default is measured for the default model, whose distances are close together: on real notes the best passage of a question
    /// that the notes answer is at 0.10 to 0.16 and the best passage of a question that nothing answers is at 0.23 to 0.26. Another model needs its own measure.
    /// </summary>
    public double MaxDistance { get; set; } = 0.2;

    /// <summary>Whether the feature is wanted at all: a model folder is given.</summary>
    public bool Wanted => !string.IsNullOrWhiteSpace(ModelFolder);
}

public sealed class EmbeddingOptionsValidator : IValidateOptions<EmbeddingOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        if (!KnownModels.TryGet(options.Model, out _))
        {
            return ValidateOptionsResult.Fail($"Embeddings:Model (MNEMARR__EMBEDDINGS__MODEL) must be one of: {string.Join(", ", KnownModels.Identifiers)}.");
        }

        if (options.Wanted && string.IsNullOrWhiteSpace(options.ExtensionPath))
        {
            return ValidateOptionsResult.Fail("Embeddings:ExtensionPath (MNEMARR__EMBEDDINGS__EXTENSIONPATH) is required when a model folder is given: it is the sqlite-vec library.");
        }

        if (options.Threads is < 1 or > 64 || options.BatchSize is < 1 or > 64 || options.MaxTokens is < 16 or > 8192 || options.IdleSeconds is < 1 or > 3600)
        {
            return ValidateOptionsResult.Fail("Embeddings: Threads is 1 to 64, BatchSize 1 to 64, MaxTokens 16 to 8192 and IdleSeconds 1 to 3600.");
        }

        return options.MaxDistance is > 0 and <= 2
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Embeddings:MaxDistance must be above 0 and at most 2.");
    }
}
