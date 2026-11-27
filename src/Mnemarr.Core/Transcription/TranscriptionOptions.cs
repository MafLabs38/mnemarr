using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Transcription;

/// <summary>
/// Settings of the dictation of the chat, from the <c>Transcription</c> section (ADR 0047). Off while no provider is named: turning it on sends what a person dictates to the
/// transcription service the operator names, and nowhere else.
/// </summary>
public sealed class TranscriptionOptions
{
    /// <summary>The name of an <see cref="ITranscriptionProvider"/>, or empty for no dictation. Built in: <c>OpenAiCompatible</c>.</summary>
    public string? Provider { get; set; }

    /// <summary>The model of the deployment, used when an administrator chose none in the interface; one of the catalog, or the first of the catalog applies.</summary>
    public string? Model { get; set; }

    /// <summary>A language hint (an ISO 639-1 code such as <c>fr</c>); without it, the model detects the language.</summary>
    public string? Language { get; set; }

    /// <summary>A YAML file that lists the models to choose from (<c>models: [{ id, label, pricePerMinute }]</c>); without it, the provider's own list.</summary>
    public string? ModelsFile { get; set; }

    /// <summary>
    /// The most <see cref="MaxMegabytes"/> can be: Mnemarr's own guard on the memory a recording takes (it is held in memory, never written to disk), which is also the
    /// limit of OpenAI's service. A local server that takes longer recordings still gets at most this much.
    /// </summary>
    public const int LargestMegabytes = 25;

    /// <summary>The largest recording a person can send, in megabytes.</summary>
    public int MaxMegabytes { get; set; } = 10;

    /// <summary>How many recordings one person can send in 24 hours, whatever happened to them.</summary>
    public int MaxPerPersonPerDay { get; set; } = 100;

    public OpenAiCompatibleTranscriptionOptions OpenAiCompatible { get; set; } = new();

    public bool Enabled => !string.IsNullOrWhiteSpace(Provider);

    public long MaxBytes => MaxMegabytes * 1024L * 1024L;
}

/// <summary>The settings of the <c>OpenAiCompatible</c> provider: any service that answers <c>POST {BaseUrl}/audio/transcriptions</c> (OpenAI, a local Whisper server, a gateway).</summary>
public sealed class OpenAiCompatibleTranscriptionOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>A file that holds the key (a secret mounted as a file, never an environment variable). A local server without a key leaves it empty.</summary>
    public string? KeyFile { get; set; }

    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class TranscriptionOptionsValidator : IValidateOptions<TranscriptionOptions>
{
    private readonly ITranscriptionProvider[] _providers;

    public TranscriptionOptionsValidator(IEnumerable<ITranscriptionProvider> providers) => _providers = [.. providers];

    public ValidateOptionsResult Validate(string? name, TranscriptionOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var provider = TranscriptionProviders.Find(_providers, options.Provider);
        var failure = provider is null
            ? $"Transcription:Provider (MNEMARR__TRANSCRIPTION__PROVIDER) is one of: {string.Join(", ", _providers.Select(known => known.Name))}, or empty for no dictation."
            : provider.Validate(options);
        if (failure is not null)
        {
            return ValidateOptionsResult.Fail(failure);
        }

        if (options.MaxMegabytes is < 1 or > TranscriptionOptions.LargestMegabytes || options.MaxPerPersonPerDay is < 1 or > 10_000)
        {
            return ValidateOptionsResult.Fail(
                $"Transcription: MaxMegabytes is 1 to {TranscriptionOptions.LargestMegabytes} (Mnemarr holds a recording in memory), MaxPerPersonPerDay 1 to 10000.");
        }

        return options.Language is { Length: > 0 } language && (language.Length != 2 || !language.All(char.IsAsciiLetterLower))
            ? ValidateOptionsResult.Fail("Transcription:Language is an ISO 639-1 code in lower case, such as fr or en, or empty.")
            : ValidateOptionsResult.Success;
    }
}
