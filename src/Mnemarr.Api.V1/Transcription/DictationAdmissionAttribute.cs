using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Transcription;

namespace Mnemarr.Api.V1.Transcription;

/// <summary>
/// Refuses a recording before its form is read (a resource filter runs before model binding): dictation off, a key of a service, a person past the daily cap, a body larger than
/// what this Mnemarr takes (<c>Transcription:MaxMegabytes</c>, and not only the largest the settings allow). The limit is also set on the request itself, so that a body
/// without a length stops being read at it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class DictationAdmissionAttribute : Attribute, IAsyncResourceFilter
{
    /// <summary>Room for the form around the recording: the boundaries, the headers of the part, the name of the file.</summary>
    internal const long FormRoom = 64 * 1024;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var transcription = http.RequestServices.GetRequiredService<TranscriptionService>();
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
        }
        catch (RejectedException rejected)
        {
            context.Result = RejectedFilter.ResultOf(http, rejected);
            return;
        }

        var limit = transcription.MaxBytes + FormRoom;
        if (http.Request.ContentLength > limit)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = $"A recording is at most {transcription.MaxMegabytes} MB.",
                Type = "urn:mnemarr:problem:recording-too-large",
            })
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge,
            };
            return;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
        {
            body.MaxRequestBodySize = limit;
        }

        await next();
    }
}
