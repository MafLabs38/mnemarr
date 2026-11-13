using Mnemarr.Api.V1.Security;

namespace Mnemarr.Host.Http;

public static class SecurityHeaders
{
    // Scripts, fonts, connections, frames and objects come from the application only. Styles may also be inline, which is what Mermaid
    // needs to measure and draw a diagram (ADR 0021); a style cannot run anything, and a note cannot put one in the page (no raw HTML).
    private const string ContentSecurityPolicy =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    /// <summary>
    /// Conservative headers on every response, errors included: they are set when the response starts, because the
    /// exception handler clears the response, headers and all, before it writes its own.
    /// HSTS belongs to the reverse proxy that terminates TLS.
    /// </summary>
    public static IApplicationBuilder UseMnemarrSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                // An endpoint that says it keeps its own headers (the logo of the identity: an image that is the same until its hash changes, with the strictest policy, kept by the browser
                // of the person; ADR 0044) does so when it is served. An error of the same endpoint, a refusal or a failure, has the application's policy and is not cached, like every other answer.
                var logo = context.Response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status304NotModified
                    && context.GetEndpoint()?.Metadata.GetMetadata<KeepsItsOwnHeadersAttribute>() is not null;
                if (!logo)
                {
                    headers["Content-Security-Policy"] = ContentSecurityPolicy;
                }

                if (ApiPaths.IsApi(context.Request.Path) && !logo)
                {
                    // What the API says is for the person who asked, and sometimes once (the secret of a new key): no cache keeps it.
                    headers.CacheControl = "no-store";
                    headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            });
            return next();
        });
}
