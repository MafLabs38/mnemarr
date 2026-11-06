using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Git;

/// <summary>Settings of the Git sources, from the <c>Git</c> section (see docs/configuration.md).</summary>
public sealed class GitOptions
{
    /// <summary>How often every Git source is brought up to date, in seconds.</summary>
    public int SyncIntervalSeconds { get; set; } = 300;

    /// <summary>The address on the commits Mnemarr makes: the author is the account that wrote the note, and this is not a person's mailbox.</summary>
    public string CommitEmail { get; set; } = "mnemarr@localhost";

    /// <summary>How long one sync of one source may take, in seconds, before it is given up (the next interval tries again).</summary>
    public int SyncTimeoutSeconds { get; set; } = 600;

    /// <summary>How long the network steps of one change of a note (the pull before it, the push after it) may take, in seconds: a person or an assistant waits for them, and the source is locked meanwhile.</summary>
    public int WriteTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// The hosts a source may be cloned from. <b>Empty means none</b>: declaring a source is how an API user asks Mnemarr to connect somewhere
    /// (and to send a secret there), so the operator says where before any Git source is accepted.
    /// </summary>
    public string[] Hosts { get; set; } = [];

    /// <summary>The credentials an operator offers to sources, by name: a source says which one it uses, and never holds one.</summary>
    public Dictionary<string, GitCredentialOptions> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An access token to one host. It is bound to that host: a source cannot send it anywhere else, whoever declares it. The token is in the
/// environment (<c>Token</c>) or in a file (<c>TokenFile</c>, read at each sync, so that it can be rotated without a restart).
/// </summary>
public sealed class GitCredentialOptions
{
    public string Host { get; set; } = string.Empty;

    /// <summary>The user name sent with the token: GitHub, GitLab and Gitea accept any name with a token.</summary>
    public string Username { get; set; } = "x-access-token";

    public string? Token { get; set; }

    public string? TokenFile { get; set; }
}

public sealed class GitOptionsValidator : IValidateOptions<GitOptions>
{
    public ValidateOptionsResult Validate(string? name, GitOptions options)
    {
        if (options.SyncIntervalSeconds is < 10 or > 86_400)
        {
            return ValidateOptionsResult.Fail("Git:SyncIntervalSeconds (MNEMARR__GIT__SYNCINTERVALSECONDS) must be between 10 and 86400.");
        }

        if (string.IsNullOrWhiteSpace(options.CommitEmail) || !options.CommitEmail.Contains('@', StringComparison.Ordinal) || options.CommitEmail.Any(char.IsWhiteSpace))
        {
            return ValidateOptionsResult.Fail("Git:CommitEmail (MNEMARR__GIT__COMMITEMAIL) must be an address, such as mnemarr@localhost.");
        }

        if (options.WriteTimeoutSeconds is < 5 or > 600)
        {
            return ValidateOptionsResult.Fail("Git:WriteTimeoutSeconds (MNEMARR__GIT__WRITETIMEOUTSECONDS) must be between 5 and 600.");
        }

        if (options.SyncTimeoutSeconds is < 10 or > 3600)
        {
            return ValidateOptionsResult.Fail("Git:SyncTimeoutSeconds (MNEMARR__GIT__SYNCTIMEOUTSECONDS) must be between 10 and 3600.");
        }

        if (options.Hosts.Any(host => string.IsNullOrWhiteSpace(host) || host.Contains('/', StringComparison.Ordinal) || host.Contains(':', StringComparison.Ordinal)))
        {
            return ValidateOptionsResult.Fail("Git:Hosts (MNEMARR__GIT__HOSTS__0, ...) must be host names, without a scheme, a port or a path.");
        }

        foreach (var (credential, settings) in options.Credentials)
        {
            if (string.IsNullOrWhiteSpace(settings.Host) || settings.Host.Contains('/', StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Fail($"Git:Credentials:{credential}:Host must be the host name the token is for.");
            }

            if (string.IsNullOrWhiteSpace(settings.Token) == string.IsNullOrWhiteSpace(settings.TokenFile))
            {
                return ValidateOptionsResult.Fail($"Git:Credentials:{credential} must have a Token or a TokenFile, and not both.");
            }

            if (!string.IsNullOrWhiteSpace(settings.TokenFile) && !Path.IsPathFullyQualified(settings.TokenFile))
            {
                return ValidateOptionsResult.Fail($"Git:Credentials:{credential}:TokenFile must be an absolute path.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
