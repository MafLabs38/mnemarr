using Microsoft.Extensions.Options;
using Mnemarr.Core.Embeddings;

namespace Mnemarr.Embedder;

/// <summary>Settings of the embedding server (ADR 0040), from the <c>Embedder</c> section. The model is the one of the <c>Embeddings</c> section, the same files as Mnemarr's.</summary>
public sealed class EmbedderOptions
{
    /// <summary>A file with the key a client must present, one line: a secret, never a value in the configuration. Required.</summary>
    public string? KeyFile { get; set; }

    /// <summary>The most texts one request may hold.</summary>
    public int MaxTexts { get; set; } = 64;

    /// <summary>The longest a text may be, in characters: a passage is about a thousand, a question at most that.</summary>
    public int MaxTextChars { get; set; } = 8000;

    /// <summary>The largest request body, in bytes.</summary>
    public int MaxBodyBytes { get; set; } = 2 * 1024 * 1024;
}

public sealed class EmbedderOptionsValidator : IValidateOptions<EmbedderOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbedderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.KeyFile))
        {
            return ValidateOptionsResult.Fail("Embedder:KeyFile (MNEMARR__EMBEDDER__KEYFILE) is required: the file that holds the key clients must present.");
        }

        if (!File.Exists(options.KeyFile))
        {
            return ValidateOptionsResult.Fail("Embedder:KeyFile does not name a file that exists.");
        }

        return options.MaxTexts is < 1 or > 1024 || options.MaxTextChars is < 100 or > 100_000 || options.MaxBodyBytes is < 1024 or > 64 * 1024 * 1024
            ? ValidateOptionsResult.Fail("Embedder: MaxTexts is 1 to 1024, MaxTextChars 100 to 100000 and MaxBodyBytes 1 KiB to 64 MiB.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// The settings of the model on the server, checked at start like Mnemarr checks them: the same ranges (a batch of 0 would never end), and a model it knows. The vector library and the
/// <c>Remote</c> section are Mnemarr's, not the server's.
/// </summary>
public sealed class EmbedderModelValidator : IValidateOptions<EmbeddingOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        if (!KnownModels.TryGet(options.Model, out _))
        {
            return ValidateOptionsResult.Fail($"Embeddings:Model (MNEMARR__EMBEDDINGS__MODEL) must be one of: {string.Join(", ", KnownModels.Identifiers)}.");
        }

        return EmbeddingOptionsValidator.RangeProblem(options) is { } range ? ValidateOptionsResult.Fail(range) : ValidateOptionsResult.Success;
    }
}
