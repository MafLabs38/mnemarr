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

    public const int ProgramDownloadPermits = 4;

    public static IServiceCollection AddMnemarrRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddConcurrencyLimiter(ProgramDownload, limit =>
            {
                limit.PermitLimit = ProgramDownloadPermits;
                limit.QueueLimit = 0;
            });
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "5";
                return ValueTask.CompletedTask;
            };
        });
}
