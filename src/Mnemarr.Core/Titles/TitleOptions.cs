using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Titles;

/// <summary>
/// Settings of the titles written by a model, from the <c>Titles</c> section (ADR 0028). Off by default: turning it on sends the start and the end of
/// conversations (already masked) to the model of the provider, which is text that leaves the host.
/// </summary>
public sealed class TitleOptions
{
    /// <summary>Whether a model writes the titles and the summaries of conversations. Off: a title is the first sentence, or what a person typed.</summary>
    public bool Enabled { get; set; }

    /// <summary>A file that holds the API key of the provider (a secret mounted as a file, never an environment variable).</summary>
    public string? ApiKeyFile { get; set; }

    public string Model { get; set; } = "claude-haiku-5-5";

    /// <summary>The address of the provider's API; HTTPS.</summary>
    public string Endpoint { get; set; } = "https://api.anthropic.com";

    /// <summary>A conversation is given a title once nothing has been added to it for this many minutes.</summary>
    public int IdleMinutes { get; set; } = 10;

    /// <summary>How many conversations are titled in one pass: the cost of a pass is bounded.</summary>
    public int PerPass { get; set; } = 5;

    public int PassSeconds { get; set; } = 60;

    /// <summary>A conversation the model failed to title this many times is left with its first sentence.</summary>
    public int MaxTries { get; set; } = 3;

    /// <summary>How much of a conversation is sent to the model, in characters.</summary>
    public int MaxChars { get; set; } = 6000;
}

public sealed class TitleOptionsValidator : IValidateOptions<TitleOptions>
{
    public ValidateOptionsResult Validate(string? name, TitleOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKeyFile))
        {
            return ValidateOptionsResult.Fail("Titles:ApiKeyFile (MNEMARR__TITLES__APIKEYFILE) is needed when Titles:Enabled is on: a file that holds the API key.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return ValidateOptionsResult.Fail("Titles:Model (MNEMARR__TITLES__MODEL) is needed.");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return ValidateOptionsResult.Fail("Titles:Endpoint (MNEMARR__TITLES__ENDPOINT) must be an https address: the key is sent with every call.");
        }

        if (options.IdleMinutes is < 1 or > 1440 || options.PerPass is < 1 or > 50 || options.PassSeconds is < 10 or > 3600 || options.MaxTries is < 1 or > 10 || options.MaxChars is < 500 or > 50_000)
        {
            return ValidateOptionsResult.Fail("Titles: IdleMinutes is 1 to 1440, PerPass 1 to 50, PassSeconds 10 to 3600, MaxTries 1 to 10, MaxChars 500 to 50000.");
        }

        return ValidateOptionsResult.Success;
    }
}
