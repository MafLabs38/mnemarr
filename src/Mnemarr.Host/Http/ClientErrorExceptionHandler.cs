using Microsoft.AspNetCore.Diagnostics;

namespace Mnemarr.Host.Http;

/// <summary>
/// A request the server could not read (a body larger than the limit of the call, a malformed request) is the caller's mistake, not the
/// server's: it gets the status the server found, as a problem document, and never its cause.
/// </summary>
public sealed class ClientErrorExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problems;

    public ClientErrorExceptionHandler(IProblemDetailsService problems)
    {
        _problems = problems;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException bad)
        {
            return false;
        }

        httpContext.Response.StatusCode = bad.StatusCode;
        return await _problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = bad.StatusCode, Title = "The request cannot be read." },
        });
    }
}
