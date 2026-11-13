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
                // A response that has said its own policy keeps it: the logo of the identity is an image served with the strictest one (ADR 0044).
                if (!headers.ContainsKey("Content-Security-Policy"))
                {
                    headers["Content-Security-Policy"] = ContentSecurityPolicy;
                }

                if (ApiPaths.IsApi(context.Request.Path) && !headers.ContainsKey("Cache-Control"))
                {
                    // What the API says is for the person who asked, and sometimes once (the secret of a new key): no cache keeps it. A response that has said how long it may be kept
                    // (an image that is the same until its hash changes) keeps what it said, private to the browser of the person.
                    headers.CacheControl = "no-store";
                    headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            });
            return next();
        });
}
