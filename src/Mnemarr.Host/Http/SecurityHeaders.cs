namespace Mnemarr.Host.Http;

public static class SecurityHeaders
{
    // Scripts, fonts, connections, frames and objects come from the application only. Styles may also be inline, which is what Mermaid
    // needs to measure and draw a diagram (ADR 0021); a style cannot run anything, and a note cannot put one in the page (no raw HTML).
    private const string ContentSecurityPolicy =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    private const string IdentityLogoPath = "/api/v1/identity/logo";

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
                // The one answer that keeps what it said itself: the logo of the identity, an image that is the same until its hash changes, served with the strictest policy and kept
                // by the browser of the person (ADR 0044). Every other answer has the application's policy and is not cached, whatever its controller wrote.
                var logo = context.Request.Path.Equals(IdentityLogoPath, StringComparison.Ordinal);
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
