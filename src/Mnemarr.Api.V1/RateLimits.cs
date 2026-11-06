using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Mnemarr.Api.V1;

/// <summary>
/// The brakes of the endpoints that anyone on the Internet may call (ADR 0029). The per-address brake belongs to the proxy, which sees the real address
/// of a caller; what Mnemarr limits itself is the one anonymous thing that costs something: writing a program of several megabytes.
/// </summary>
public static class RateLimits
{
    /// <summary>The policy of the download of a program: at most <see cref="ProgramDownloadPermits"/> at the same time, the others are refused at once.</summary>
    public const string ProgramDownload = "program-download";

    private const int ProgramDownloadPermits = 4;

    /// <summary>What a refused caller is told to wait: a program is written in a few seconds.</summary>
    private const int RetryAfterSeconds = 5;

    public static IServiceCollection AddMnemarrRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddConcurrencyLimiter(ProgramDownload, limit =>
            {
                limit.PermitLimit = ProgramDownloadPermits;
                limit.QueueLimit = 0;
            });
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // The same shape as every other refusal of the API.
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = StatusCodes.Status429TooManyRequests, Title = "Programs are being written for other machines: try again in a few seconds." },
                    cancellationToken);
            };
        });
}
