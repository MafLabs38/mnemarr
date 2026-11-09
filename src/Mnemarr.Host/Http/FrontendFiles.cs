using Microsoft.AspNetCore.StaticFiles;

namespace Mnemarr.Host.Http;

public static class FrontendFiles
{
    private static readonly StaticFileOptions Caching = new() { OnPrepareResponse = Cache };

    /// <summary>
    /// The page that names the scripts is looked at again each time (a cheap check of its tag), so that a new version is shown at once and not hours later;
    /// the scripts and styles carry a hash in their name and never change under it, so they are kept for good.
    /// </summary>
    private static void Cache(StaticFileResponseContext context)
    {
        var headers = context.Context.Response.Headers;
        if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-cache";
        }
        else if (context.Context.Request.Path.StartsWithSegments("/assets"))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
    }

    /// <summary>Serves the built frontend from <c>wwwroot</c>, next to the binary.</summary>
    public static WebApplication UseMnemarrFrontendFiles(this WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles(Caching);
        return app;
    }

    /// <summary>
    /// Any other path is a route of the single-page application. An unknown path under <c>/api</c> stays a 404:
    /// an API client must never receive the HTML shell.
    /// </summary>
    public static WebApplication MapMnemarrFrontendFallback(this WebApplication app)
    {
        app.MapFallback($"{ApiPaths.Prefix}/{{**path}}", () => Results.NotFound());
        app.MapFallbackToFile("index.html", Caching).AllowAnonymous();
        return app;
    }
}
