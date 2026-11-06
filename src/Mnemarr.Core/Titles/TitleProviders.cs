using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Titles;

/// <summary>
/// A way to have a title written, by name (<c>Titles:Provider</c>). Another model, another API or another tool is one class that implements this and one line,
/// <c>services.AddSingleton&lt;ITitleProvider, MyProvider&gt;()</c>: it says what settings it needs and builds the <see cref="ITitleWriter"/> that talks to it (ADR 0028).
/// </summary>
public interface ITitleProvider
{
    /// <summary>The value of <c>Titles:Provider</c> that selects it; not case sensitive.</summary>
    string Name { get; }

    /// <summary>What is wrong with the settings for this provider, or null when they are right. Called only when titles are turned on.</summary>
    string? Validate(TitleOptions options);

    ITitleWriter Create(IOptions<TitleOptions> options);
}

/// <summary>The providers that come with Mnemarr, and how one is found by name.</summary>
public static class TitleProviders
{
    public const string Anthropic = "Anthropic";
    public const string OpenAi = "OpenAI";
    public const string Agent = "Agent";

    public static IReadOnlyList<ITitleProvider> BuiltIn { get; } = [new AnthropicTitleProvider(), new OpenAiTitleProvider(), new AgentTitleProvider()];

    public static ITitleProvider? Find(IEnumerable<ITitleProvider> providers, string? name) =>
        providers.FirstOrDefault(provider => string.Equals(provider.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static ITitleWriter Select(IEnumerable<ITitleProvider> providers, IOptions<TitleOptions> options) =>
        Find(providers, options.Value.Provider)?.Create(options) ?? new UnavailableTitleWriter(options.Value.Provider);
}

/// <summary>The writer when no provider has the name that was set: titles are off in that case (the validation refuses it when they are on), and a call says why.</summary>
internal sealed class UnavailableTitleWriter(string provider) : ITitleWriter
{
    public Task<GeneratedTitle?> WriteAsync(string transcript, CancellationToken cancellationToken) =>
        throw new TitleWriterException($"There is no title provider called '{provider}'.");
}

public sealed class AnthropicTitleProvider : ITitleProvider
{
    public string Name => TitleProviders.Anthropic;

    public string? Validate(TitleOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKeyFile))
        {
            return "Titles:ApiKeyFile (MNEMARR__TITLES__APIKEYFILE) is needed with the Anthropic provider: a file that holds the API key.";
        }

        return !Uri.TryCreate(options.Endpoint ?? AnthropicTitleWriter.DefaultEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            ? "Titles:Endpoint (MNEMARR__TITLES__ENDPOINT) must be an https address: the key is sent with every call."
            : null;
    }

    public ITitleWriter Create(IOptions<TitleOptions> options) => new AnthropicTitleWriter(options);
}

public sealed class OpenAiTitleProvider : ITitleProvider
{
    public string Name => TitleProviders.OpenAi;

    public string? Validate(TitleOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            return "Titles:Endpoint (MNEMARR__TITLES__ENDPOINT) is needed with the OpenAI provider: the address of the API, such as https://api.openai.com or http://localhost:11434 for a local model.";
        }

        // A key is never sent in clear over the network; a local server without a key may use http.
        return !string.IsNullOrWhiteSpace(options.ApiKeyFile) && endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback
            ? "Titles:Endpoint (MNEMARR__TITLES__ENDPOINT) must be an https address when a key is used, unless it is on this host."
            : null;
    }

    public ITitleWriter Create(IOptions<TitleOptions> options) => new OpenAiTitleWriter(options);
}

public sealed class AgentTitleProvider : ITitleProvider
{
    public string Name => TitleProviders.Agent;

    public string? Validate(TitleOptions options)
    {
        if (!Uri.TryCreate(options.AgentUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return "Titles:AgentUrl (MNEMARR__TITLES__AGENTURL) is needed with the Agent provider: the address of the companion agent.";
        }

        return string.IsNullOrWhiteSpace(options.AgentSecretFile)
            ? "Titles:AgentSecretFile (MNEMARR__TITLES__AGENTSECRETFILE) is needed with the Agent provider: a file that holds the secret shared with the agent."
            : null;
    }

    public ITitleWriter Create(IOptions<TitleOptions> options) => new AgentTitleWriter(options);
}
