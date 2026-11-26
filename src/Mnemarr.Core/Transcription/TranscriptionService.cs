using Microsoft.Extensions.Options;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Transcription;

/// <summary>Where the model in use comes from: what an administrator chose, the deployment's setting, or the first model of the catalog.</summary>
public enum ModelLayer
{
    Interface,
    Deployment,
    Catalog,
}

/// <summary>
/// What an administrator sees of the dictation: whether it is on, where the sound goes, the catalog, the model in use and where it comes from, and a sentence when the model
/// that was asked for is not in the catalog any more.
/// </summary>
public sealed record TranscriptionStatus(
    bool Enabled,
    string? Provider,
    string? Destination,
    TranscriptionCatalog Catalog,
    TranscriptionModel Model,
    ModelLayer Layer,
    string? ModelProblem,
    string? Language,
    int MaxMegabytes,
    int MaxPerPersonPerDay);

/// <summary>
/// The dictation of the chat (ADR 0047): a person's recording goes to the transcriber with the model in use, under a cap per person per day, and every attempt is recorded and
/// audited without its text. The sound is held in memory for the call and dropped; nothing of it is stored.
/// </summary>
public sealed class TranscriptionService
{
    /// <summary>The media types a recording may have (without parameters such as <c>codecs</c>): what browsers record and the services read.</summary>
    public static readonly IReadOnlySet<string> AcceptedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audio/webm", "audio/ogg", "audio/mp4", "audio/mpeg", "audio/wav", "audio/x-wav",
    };

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan Kept = TimeSpan.FromDays(7);
    private readonly IOptions<TranscriptionOptions> _options;
    private readonly IEnumerable<ITranscriptionProvider> _providers;
    private readonly ITranscriber _transcriber;
    private readonly TranscriptionModels _models;
    private readonly ITranscriptionStore _store;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;

    public TranscriptionService(
        IOptions<TranscriptionOptions> options,
        IEnumerable<ITranscriptionProvider> providers,
        ITranscriber transcriber,
        TranscriptionModels models,
        ITranscriptionStore store,
        IAuditLog audit,
        TimeProvider time)
    {
        _options = options;
        _providers = providers;
        _transcriber = transcriber;
        _models = models;
        _store = store;
        _audit = audit;
        _time = time;
    }

    public bool Enabled => _options.Value.Enabled;

    public TranscriptionStatus Status()
    {
        var options = _options.Value;
        var catalog = _models.Current();
        var (model, layer, problem) = Resolve(catalog, options);
        return new TranscriptionStatus(
            options.Enabled,
            options.Enabled ? options.Provider!.Trim() : null,
            options.Enabled ? TranscriptionProviders.Find(_providers, options.Provider)?.Destination(options) : null,
            catalog,
            model,
            layer,
            problem,
            options.Language,
            options.MaxMegabytes,
            options.MaxPerPersonPerDay);
    }

    /// <summary>Chooses the model to use, among the catalog's; null goes back to the deployment's. Audited.</summary>
    public TranscriptionStatus ChooseModel(string? model, string actor)
    {
        var wanted = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (wanted is not null && _models.Current().Models.All(known => known.Id != wanted))
        {
            throw new RejectedException(Problem.Invalid, "The model is not one of the catalog.");
        }

        _store.ChooseModel(wanted);
        _audit.Append(actor, "transcription.model", "settings", wanted ?? "(the deployment's)");
        return Status();
    }

    /// <summary>Turns one recording into text, or refuses it (off, a format it does not take, too big, the cap reached). A service that fails is a result, not an exception.</summary>
    public async Task<TranscriptionResult> TranscribeAsync(string actor, ReadOnlyMemory<byte> audio, string? contentType, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            throw new RejectedException(Problem.NotFound, "Dictation is not turned on.");
        }

        var mediaType = contentType?.Split(';')[0].Trim() ?? string.Empty;
        if (!AcceptedTypes.Contains(mediaType))
        {
            throw new RejectedException(Problem.Invalid, "The recording is not in a format the dictation takes (webm, ogg, mp4, mp3 or wav).");
        }

        if (audio.Length == 0 || audio.Length > options.MaxBytes)
        {
            throw new RejectedException(Problem.Invalid, $"A recording is at most {options.MaxMegabytes} MB.");
        }

        var now = _time.GetUtcNow();
        if (_store.CountSince(actor, now - Day) >= options.MaxPerPersonPerDay)
        {
            throw new RejectedException(Problem.Busy, $"You have sent {options.MaxPerPersonPerDay} recordings in 24 hours, the most this Mnemarr allows: try again later.", TimeSpan.FromHours(1));
        }

        var (model, _, _) = Resolve(_models.Current(), options);
        var result = await _transcriber.TranscribeAsync(new TranscriptionRequest(audio, mediaType, model.Id, options.Language), cancellationToken);
        _store.Prune(now - Kept);
        _store.Record(new TranscriptionAttempt(actor, now, audio.Length, model.Id, result.Succeeded, result.Seconds, result.Reason));
        _audit.Append(actor, "transcription.used", model.Id, result.Succeeded ? $"{audio.Length} bytes" : $"{audio.Length} bytes; failed: {result.Reason}");
        return result;
    }

    /// <summary>The model in use: the interface's choice, then the deployment's, each only when the catalog has it, then the catalog's first.</summary>
    private (TranscriptionModel Model, ModelLayer Layer, string? Problem) Resolve(TranscriptionCatalog catalog, TranscriptionOptions options)
    {
        string? problem = null;
        foreach (var (wanted, layer) in new[] { (_store.ChosenModel(), ModelLayer.Interface), (options.Model, ModelLayer.Deployment) })
        {
            if (string.IsNullOrWhiteSpace(wanted))
            {
                continue;
            }

            if (catalog.Models.FirstOrDefault(model => model.Id == wanted.Trim()) is { } found)
            {
                return (found, layer, problem);
            }

            problem ??= $"The model '{wanted.Trim()}' is not in the catalog any more: another one is used.";
        }

        return (catalog.Models[0], ModelLayer.Catalog, problem);
    }
}
