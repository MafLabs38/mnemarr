using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Git;

/// <summary>A user name and a token, for one host.</summary>
public sealed record GitSecret(string Username, string Token);

/// <summary>
/// The credentials the operator offers (<see cref="GitOptions"/>): which one a name is, which host it is for, and its token, read when it is
/// needed. The token never leaves this class except to the engine, that sends it to the host it is bound to.
/// </summary>
public sealed class GitCredentials
{
    private readonly IOptions<GitOptions> _options;

    public GitCredentials(IOptions<GitOptions> options)
    {
        _options = options;
    }

    /// <summary>The host a credential is bound to, or null when there is no credential of this name.</summary>
    public string? HostOf(string name) => _options.Value.Credentials.TryGetValue(name, out var credential) ? credential.Host.Trim().ToLowerInvariant() : null;

    /// <summary>The secret of a credential, or null when it is not there or its file cannot be read (said by the caller as <c>credential-missing</c>).</summary>
    public GitSecret? Secret(string name)
    {
        if (!_options.Value.Credentials.TryGetValue(name, out var credential))
        {
            return null;
        }

        try
        {
            var token = (string.IsNullOrWhiteSpace(credential.TokenFile) ? credential.Token : File.ReadAllText(credential.TokenFile))?.Trim();
            return string.IsNullOrEmpty(token) ? null : new GitSecret(credential.Username, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
