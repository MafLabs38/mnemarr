namespace Mnemarr.Host.Http;

public static class FrontendFiles
{
    /// <summary>Serves the built frontend from <c>wwwroot</c>, next to the binary.</summary>
    public static WebApplication UseMnemarrFrontendFiles(this WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();
        return app;
    }

    /// <summary>
    /// Any other path is a route of the single-page application. An unknown path under <c>/api</c> stays a 404:
    /// an API client must never receive the HTML shell.
    /// </summary>
    public static WebApplication MapMnemarrFrontendFallback(this WebApplication app)
    {
        app.MapFallback("/api/{**path}", () => Results.NotFound());
        app.MapFallbackToFile("index.html").AllowAnonymous();
        return app;
    }
}
