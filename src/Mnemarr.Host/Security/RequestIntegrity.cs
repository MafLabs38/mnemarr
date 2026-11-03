using Microsoft.AspNetCore.Mvc;
using Mnemarr.Host.Http;
namespace Mnemarr.Host.Security;

/// <summary>
/// The protection against cross-site requests (ADR 0016), on top of <c>SameSite=Strict</c>: a write to the API that does not carry an API key
/// must carry the custom header that only the interface, and a tool that means to, sends. A browser cannot add it to a form or a link.
/// </summary>
public sealed class RequestIntegrityMiddleware
{
    public const string HeaderName = "X-Mnemarr-Request";
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };
    private readonly RequestDelegate _next;

    public RequestIntegrityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var isWrite = !SafeMethods.Contains(context.Request.Method);
        var isApi = ApiPaths.IsApi(context.Request.Path);
        if (isWrite && isApi && ApiKeyHeader.Read(context.Request) is null && !context.Request.Headers.ContainsKey(HeaderName))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(
                new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = $"A write needs the {HeaderName} header." },
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted);
        }

        return _next(context);
    }
}
