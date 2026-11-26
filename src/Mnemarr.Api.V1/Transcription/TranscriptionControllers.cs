using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Chat;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Transcription;

namespace Mnemarr.Api.V1.Transcription;

/// <summary>Whether the chat's microphone is offered, and the largest recording it may send.</summary>
public sealed record DictationResource(bool Enabled, int MaxMegabytes);

/// <summary>What a recording became.</summary>
public sealed record TranscriptResource(string Text);

/// <summary>A model of the catalog, with its price per minute in dollars when it is known.</summary>
public sealed record TranscriptionModelResource(string Id, string Label, decimal? PricePerMinute);

/// <summary>
/// The dictation as an administrator sees it: whether it is on, the provider and the host the sound is sent to, the catalog (and whether it comes from the deployment's file, with
/// the reason it could not be used), the model in use and where it comes from (<c>interface</c>, <c>deployment</c>, <c>catalog</c>), and the caps.
/// </summary>
public sealed record TranscriptionSettingsResource(
    bool Enabled,
    string? Provider,
    string? Destination,
    IReadOnlyList<TranscriptionModelResource> Models,
    bool ModelsFromFile,
    string? ModelsProblem,
    string Model,
    string ModelLayer,
    string? ModelProblem,
    string? Language,
    int MaxMegabytes,
    int MaxPerPersonPerDay)
{
    public static TranscriptionSettingsResource From(TranscriptionStatus status) => new(
        status.Enabled,
        status.Provider,
        status.Destination,
        [.. status.Catalog.Models.Select(model => new TranscriptionModelResource(model.Id, model.Label, model.PricePerMinute))],
        status.Catalog.FromFile,
        status.Catalog.Error,
        status.Model.Id,
        status.Layer.ToString().ToLowerInvariant(),
        status.ModelProblem,
        status.Language,
        status.MaxMegabytes,
        status.MaxPerPersonPerDay);
}

/// <summary>The model chosen in the interface, or null to go back to the deployment's.</summary>
public sealed record TranscriptionModelRequest(string? Model);

/// <summary>
/// The chat's dictation (ADR 0047): a person sends a recording and gets its text back; the text is never sent to the chat by Mnemarr. For people only, with <c>chat.use</c>: a key of a
/// service cannot spend the transcription key. The sound is read into memory, sent, and dropped.
/// </summary>
[ApiController]
[Authorize(Policy = Permissions.ChatUse)]
[PeopleOnly]
[Route("api/v1/chat/transcriptions")]
public sealed class TranscriptionsController : ControllerBase
{
    /// <summary>The ceiling of a request whatever the settings say (the settings allow at most 25 MB), with room for the form around the file.</summary>
    private const long MaxRequestBytes = (25L * 1024 * 1024) + (64 * 1024);

    private readonly TranscriptionService _transcription;

    public TranscriptionsController(TranscriptionService transcription) => _transcription = transcription;

    /// <summary>Whether the microphone is offered.</summary>
    [HttpGet("status")]
    [ProducesResponseType<DictationResource>(StatusCodes.Status200OK)]
    public DictationResource Status() => new(_transcription.Enabled, _transcription.Status().MaxMegabytes);

    /// <summary>Turns a recording (a multipart form, the field <c>file</c>, webm, ogg, mp4, mp3 or wav) into text. 404 when dictation is off, 429 past the daily cap, 502 when the service fails.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<TranscriptResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<TranscriptResource>> TranscribeAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (!_transcription.Enabled)
        {
            throw new RejectedException(Core.Problem.NotFound, "Dictation is not turned on.");
        }

        if (file is null || file.Length == 0)
        {
            throw new RejectedException(Core.Problem.Invalid, "The recording is required: the field file of a multipart form.");
        }

        using var content = new MemoryStream((int)file.Length);
        await file.CopyToAsync(content, cancellationToken);
        var result = await _transcription.TranscribeAsync(User.ActorName(), content.GetBuffer().AsMemory(0, (int)content.Length), file.ContentType, cancellationToken);
        return result.Succeeded
            ? new TranscriptResource(result.Text!)
            : Problem(statusCode: StatusCodes.Status502BadGateway, title: result.Reason, type: "urn:mnemarr:problem:transcription-failed");
    }
}

/// <summary>The dictation's settings, for an administrator (<c>settings.manage</c>): what is on, where the sound goes, and which model of the catalog is used.</summary>
[ApiController]
[Authorize(Policy = Permissions.SettingsManage)]
[Route("api/v1/transcription")]
public sealed class TranscriptionSettingsController : ControllerBase
{
    private readonly TranscriptionService _transcription;

    public TranscriptionSettingsController(TranscriptionService transcription) => _transcription = transcription;

    [HttpGet]
    [ProducesResponseType<TranscriptionSettingsResource>(StatusCodes.Status200OK)]
    public TranscriptionSettingsResource Get() => TranscriptionSettingsResource.From(_transcription.Status());

    /// <summary>Chooses the model among the catalog's; null goes back to the deployment's setting, then to the catalog's first. Audited.</summary>
    [HttpPut("model")]
    [ProducesResponseType<TranscriptionSettingsResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public TranscriptionSettingsResource ChooseModel(TranscriptionModelRequest request) =>
        TranscriptionSettingsResource.From(_transcription.ChooseModel(request.Model, User.ActorName()));
}
