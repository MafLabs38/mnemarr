using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Chat;

/// <summary>
/// The chat (ADR 0035), section <c>Agent</c>: <c>MNEMARR__AGENT__URL</c> (the optional assistant runner, <c>mnemarr-agent</c>, for example <c>http://mnemarr-agent:8489</c>) and
/// <c>MNEMARR__AGENT__SECRET</c> (shared with the runner). Without a URL the chat is off: its endpoints answer 404 and the page shows no chat.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public const int MinimumSecretLength = 32;

    public string? Url { get; set; }

    /// <summary>Sent to the runner as a bearer token. Never returned by the API, never logged.</summary>
    public string? Secret { get; set; }

    public const int DefaultMaxConversationsPerPerson = 50;

    /// <summary>
    /// How many conversations a person keeps (MNEMARR__AGENT__MAXCONVERSATIONSPERPERSON). The runner has one cap for everybody (MNEMARR_AGENT_MAX_CONVERSATIONS) and refuses new conversations
    /// when it is full, so this times the number of people should stay under it.
    /// </summary>
    public int MaxConversationsPerPerson { get; set; } = DefaultMaxConversationsPerPerson;

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>Fails the start on a malformed runner address or a weak secret.</summary>
public sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add("Agent:Url must be an http(s) address without credentials (MNEMARR__AGENT__URL, for example http://mnemarr-agent:8489).");
        }

        if (options.Secret is null || options.Secret.Trim().Length < AgentOptions.MinimumSecretLength)
        {
            errors.Add($"Agent:Secret must be at least {AgentOptions.MinimumSecretLength} characters when Agent:Url is set (MNEMARR__AGENT__SECRET, for example openssl rand -hex 32).");
        }

        if (options.MaxConversationsPerPerson is < 1 or > 1000)
        {
            errors.Add("Agent:MaxConversationsPerPerson must be between 1 and 1000 (MNEMARR__AGENT__MAXCONVERSATIONSPERPERSON).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
