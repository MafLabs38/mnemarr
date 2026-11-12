namespace Mnemarr.Embedder;

// Not top-level statements: they would make a type named Program, which the test assembly (it also reaches the host's Program) would then find twice.
internal static class EmbedderHost
{
    private const string LoopbackAddress = "127.0.0.1";

    private static async Task<int> Main(string[] args)
    {
        // The probe of the container: the image has no curl, so the program asks itself. It exits 0 when the model is loaded.
        if (args.Contains("--healthcheck"))
        {
            return await ProbeAsync();
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables(prefix: "MNEMARR__");
        builder.Services.AddEmbedder(builder.Configuration);

        // The largest body a request may have: a request of texts is a few kilobytes.
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
            kestrel.Limits.MaxRequestBodySize = context.Configuration.GetValue("Embedder:MaxBodyBytes", new EmbedderOptions().MaxBodyBytes));

        var app = builder.Build();
        app.MapEmbedder();
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> ProbeAsync()
    {
        if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is not { Length: > 0 } urls)
        {
            return 1;
        }

        // Where the server listens as seen from inside: a wildcard address is the local one.
        var address = urls.Split(';')[0];
        foreach (var wildcard in new[] { "0.0.0.0", "[::]", "+", "*" })
        {
            address = address.Replace(wildcard, LoopbackAddress, StringComparison.Ordinal);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await http.GetAsync(new Uri(new Uri(address), EmbedderEndpoints.HealthPath));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return 1;
        }
    }
}
