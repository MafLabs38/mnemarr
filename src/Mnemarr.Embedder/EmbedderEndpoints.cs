using Microsoft.Extensions.Options;
using Mnemarr.Core.Embeddings;

namespace Mnemarr.Embedder;

/// <summary>
/// The two endpoints of the embedding server (ADR 0040): <c>GET /health</c> says whether the model is loaded, and <c>POST /v1/embeddings</c> turns texts into vectors for a client that
/// holds the key. The server keeps nothing: no text, no vector, no log of either.
/// </summary>
public static class EmbedderEndpoints
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>The path that says whether the model is loaded; the probe of the container asks it.</summary>
    public const string HealthPath = "health";

    public static IServiceCollection AddEmbedder(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmbeddingOptions>().Bind(configuration.GetSection("Embeddings"));
        services.AddOptions<EmbedderOptions>().Bind(configuration.GetSection("Embedder")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmbedderOptions>, EmbedderOptionsValidator>();
        services.AddSingleton<EmbedderKey>();
        services.AddSingleton<OnnxEmbeddingProvider>();
        services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<OnnxEmbeddingProvider>());
        services.AddHostedService<ModelWarmUp>();
        return services;
    }

    public static WebApplication MapEmbedder(this WebApplication app)
    {
        app.MapGet("/" + HealthPath, (IEmbeddingProvider provider) => provider.Known() switch
        {
            { State: EmbeddingState.Ready } => Results.Ok(new { status = "ready" }),
            { State: EmbeddingState.Failed } => Results.Json(new { status = "failed" }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(new { status = "starting" }, statusCode: StatusCodes.Status503ServiceUnavailable),
        });

        app.MapPost("/" + RemoteEmbeddingClient.Path, Embed);
        return app;
    }

    private static async Task<IResult> Embed(HttpRequest http, RemoteEmbeddingRequest? body, EmbedderKey key, IEmbeddingProvider provider, IOptions<EmbedderOptions> options, CancellationToken cancellationToken)
    {
        var header = http.Headers.Authorization.ToString();
        if (!key.Accepts(header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? header[BearerPrefix.Length..].Trim() : null))
        {
            return Results.Unauthorized();
        }

        var limits = options.Value;
        if (body?.Texts is not { Count: > 0 } texts || texts.Count > limits.MaxTexts || texts.Any(text => text is null || text.Length > limits.MaxTextChars))
        {
            return Results.Problem("The request must hold between 1 and the allowed number of texts, none longer than the allowed length.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (body.Kind is not ("passage" or "query"))
        {
            return Results.Problem("The kind must be 'passage' or 'query'.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(body.Model, provider.ModelId, StringComparison.Ordinal))
        {
            // The vectors of two models are not the same space: a client that expects another model is told, not given the wrong ones.
            return Results.Problem($"This server computes {provider.ModelId}.", statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            var vectors = await Task.Run(() => body.Kind == "query" ? [.. texts.Select(provider.EmbedQuery)] : provider.EmbedPassages(texts), cancellationToken);
            return Results.Ok(new RemoteEmbeddingResponse(provider.ModelId, provider.Dimension, provider.Device, vectors));
        }
        catch (EmbeddingException)
        {
            // The model failed or is busy: the client computes on its own CPU. What went wrong is for this server's log, which holds no text.
            return Results.Problem("The model could not compute the vectors.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

/// <summary>
/// Loads the model when the server starts, so that the first request does not wait for it and <c>/health</c> tells the truth. It also reads the key then (it is a parameter), so that a key file
/// that is wrong stops the server at start and not at the first request.
/// </summary>
public sealed class ModelWarmUp(IEmbeddingProvider provider, EmbedderKey key) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => key is null ? Task.CompletedTask : Task.Run(() => provider.Status(), stoppingToken);
}
