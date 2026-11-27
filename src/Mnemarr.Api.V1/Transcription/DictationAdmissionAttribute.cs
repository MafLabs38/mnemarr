using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Transcription;

namespace Mnemarr.Api.V1.Transcription;

/// <summary>
/// Refuses a recording before its form is read (a resource filter runs before model binding): dictation off, a key of a service, a person past the daily cap, a body larger than
/// what this Mnemarr takes (<c>Transcription:MaxMegabytes</c>, and not only the largest the settings allow), all the places for recordings taken
/// (<see cref="TranscriptionService.InFlight"/>). The limit is also set on the request itself, so that a body without a length stops being read at it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class DictationAdmissionAttribute : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    /// <summary>
    /// After <c>[RequestSizeLimit]</c> and <c>[RequestFormLimits]</c> (900), whose ceiling is the largest the settings allow: the limit this filter sets on the request, the
    /// size this Mnemarr takes, is the one that holds. Still before the form is read, which model binding does after every resource filter.
    /// </summary>
    public int Order => 1000;

    /// <summary>Room for the form around the recording: the boundaries, the headers of the part, the name of the file.</summary>
    internal const long FormRoom = 64 * 1024;

    /// <summary>The slowest a recording may arrive after its first 10 seconds: 4 KB a second, far below a phone on a poor network (Kestrel's own floor is 240 bytes a second).</summary>
    private static readonly MinDataRate LeastRate = new(bytesPerSecond: 4096, gracePeriod: TimeSpan.FromSeconds(10));

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var transcription = http.RequestServices.GetRequiredService<TranscriptionService>();
        IDisposable place;
        try
        {
            if (http.User.Person().Kind != AccountKind.User)
            {
                context.Result = new ObjectResult(new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "The chat is for people: a key of a service cannot use it." })
                {
                    StatusCode = StatusCodes.Status403Forbidden,
                };
                return;
            }

            transcription.AdmitPerson(http.User.ActorName());
            if (!Fits(http, transcription))
            {
                context.Result = TooLarge(transcription);
                return;
            }

            // Last, so that a refused recording never holds a place.
            place = transcription.Enter(http.User.ActorName());
        }
        catch (RejectedException rejected)
        {
            context.Result = RejectedFilter.ResultOf(http, rejected);
            return;
        }

        using (place)
        {
            await next();
        }
    }

    /// <summary>
    /// Whether the body is not announced larger than the limit. The limit is also set on the request, for a body without a length, and so is a least rate: a body sent
    /// slowly on purpose cannot hold a place for hours.
    /// </summary>
    private static bool Fits(HttpContext http, TranscriptionService transcription)
    {
        var limit = transcription.MaxBytes + FormRoom;
        if (http.Request.ContentLength > limit)
        {
            return false;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
        {
            body.MaxRequestBodySize = limit;
        }

        // Kestrel takes a least rate per request over HTTP/1.1 only (it refuses one over HTTP/2 and 3, which have their own flow control).
        if (HttpProtocol.IsHttp11(http.Request.Protocol) && http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
        {
            rate.MinDataRate = LeastRate;
        }

        return true;
    }

    private static ObjectResult TooLarge(TranscriptionService transcription) =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Title = $"A recording is at most {transcription.MaxMegabytes} MB.",
            Type = "urn:mnemarr:problem:recording-too-large",
        })
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge,
        };
}
