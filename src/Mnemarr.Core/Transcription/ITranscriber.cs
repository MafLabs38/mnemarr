using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Transcription;

/// <summary>A recording to turn into text: its content, its media type as the browser gave it, the model of the catalog, and an optional language hint.</summary>
public sealed record TranscriptionRequest(ReadOnlyMemory<byte> Audio, string ContentType, string Model, string? Language);

/// <summary>The text, or why there is none in a short sentence of Mnemarr's own (never the service's text, never a secret); the duration when the service reports one.</summary>
public sealed record TranscriptionResult(bool Succeeded, string? Text, string? Reason, double? Seconds = null)
{
    public static TranscriptionResult Done(string text, double? seconds) => new(true, text, null, seconds);

    public static TranscriptionResult Failed(string reason) => new(false, null, reason);
}

/// <summary>Turns speech into text. The only thing the rest of Mnemarr knows about a transcription service.</summary>
public interface ITranscriber
{
    /// <summary>Never throws for a transcription that fails: it says so in the result. A cancelled call is the only exception.</summary>
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A transcription service, by name (<c>Transcription:Provider</c>). Another one is one class that implements this and one line,
/// <c>services.AddSingleton&lt;ITranscriptionProvider, MyProvider&gt;()</c> (ADR 0047), as for the notifications (ADR 0042).
/// </summary>
public interface ITranscriptionProvider
{
    /// <summary>The value of <c>Transcription:Provider</c> that selects it; not case sensitive.</summary>
    string Name { get; }

    /// <summary>What is wrong with the settings for this provider, or null when they are right. Called only when dictation is turned on.</summary>
    string? Validate(TranscriptionOptions options);

    ITranscriber Create(IOptions<TranscriptionOptions> options);

    /// <summary>What is wrong with the secrets the provider needs (a key file that cannot be read), or null. Looked at by the readiness probe, never sent anywhere.</summary>
    string? CheckSecrets(TranscriptionOptions options);

    /// <summary>The host the recordings are sent to, shown to an administrator so they know where the sound goes.</summary>
    string? Destination(TranscriptionOptions options);
}

/// <summary>The providers that come with Mnemarr, and how one is found by name.</summary>
public static class TranscriptionProviders
{
    public const string OpenAiCompatible = "OpenAiCompatible";

    public static IReadOnlyList<ITranscriptionProvider> BuiltIn { get; } = [new OpenAiCompatibleTranscriptionProvider()];

    public static ITranscriptionProvider? Find(IEnumerable<ITranscriptionProvider> providers, string? name) =>
        providers.FirstOrDefault(provider => string.Equals(provider.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static ITranscriber Select(IEnumerable<ITranscriptionProvider> providers, IOptions<TranscriptionOptions> options) =>
        Find(providers, options.Value.Provider)?.Create(options) ?? new UnavailableTranscriber();
}

internal sealed class UnavailableTranscriber : ITranscriber
{
    public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(TranscriptionResult.Failed("Dictation is not turned on."));
}
