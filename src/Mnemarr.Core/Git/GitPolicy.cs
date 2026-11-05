using Microsoft.Extensions.Options;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Git;

/// <summary>
/// What a Git source may be (ADR 0022). Declaring one is a request to connect to a host and, with a credential, to send it a secret, so
/// the address is HTTPS with nothing in it that is a secret, the host is one the operator allows, and a credential is only ever for its own host.
/// </summary>
public sealed class GitPolicy
{
    private const int MaxLength = 2048;
    private const int MaxBranchLength = 200;
    private readonly IOptions<GitOptions> _options;
    private readonly GitCredentials _credentials;

    public GitPolicy(IOptions<GitOptions> options, GitCredentials credentials)
    {
        _options = options;
        _credentials = credentials;
    }

    /// <summary>The repository in its normal form (host in lower case, no trailing slash), or a refusal that says why.</summary>
    public GitRepository Check(GitRepository repository)
    {
        var url = Address(repository.Url);
        var host = new Uri(url).Host;
        CheckHost(host);

        var credential = string.IsNullOrWhiteSpace(repository.Credential) ? null : repository.Credential.Trim();
        // The same answer for a credential that does not exist and for one that is for another host: a caller learns nothing about the credentials.
        if (credential is not null && !string.Equals(_credentials.HostOf(credential), host, StringComparison.OrdinalIgnoreCase))
        {
            throw new RejectedException(Problem.Invalid, "There is no credential of this name for the host of the repository.");
        }

        return new GitRepository(url, Branch(repository.Branch), credential);
    }

    /// <summary>Whether the operator allows this host for Git sources: none are allowed until <c>Git:Hosts</c> says which.</summary>
    internal void CheckHost(string host)
    {
        var allowed = _options.Value.Hosts;
        if (allowed.Length == 0)
        {
            throw new RejectedException(Problem.Invalid, "The operator has not allowed any host for Git sources yet (Git:Hosts).");
        }

        if (!allowed.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new RejectedException(Problem.Invalid, "The repository is on a host that the operator does not allow for sources.");
        }
    }

    /// <summary>Whether two repositories are the same one, in the form they are compared in (case of the host, a trailing slash, an empty branch).</summary>
    internal static bool Same(GitRepository? first, GitRepository? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return string.Equals(first.Url.Trim().TrimEnd('/'), second.Url.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && Trimmed(first.Branch) == Trimmed(second.Branch)
            && Trimmed(first.Credential) == Trimmed(second.Credential);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The credentials the operator offers: the policy is where a source's credential is checked, and where its secret is read at a sync.</summary>
    internal GitCredentials Credentials => _credentials;

    /// <summary>The address on the commits Mnemarr makes.</summary>
    internal string CommitEmail => _options.Value.CommitEmail;

    /// <summary>How long one sync may take.</summary>
    internal TimeSpan SyncTimeout => TimeSpan.FromSeconds(_options.Value.SyncTimeoutSeconds);

    /// <summary>How long the pull before a change of a note, and the push after it, may take each.</summary>
    internal TimeSpan WriteTimeout => TimeSpan.FromSeconds(_options.Value.WriteTimeoutSeconds);

    /// <summary>An HTTPS address with a host and a path and nothing else: no user name or password (a secret does not go in an address), no query, no fragment.</summary>
    internal static string Address(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 || uri.AbsolutePath.Length <= 1)
        {
            throw new RejectedException(Problem.Invalid, "The address of a repository is an https address with a host and a path.");
        }

        if (!uri.IsDefaultPort)
        {
            // The host check and the credential's binding are to a host: another port is another service, and would get the host's token.
            throw new RejectedException(Problem.Invalid, "The address of a repository uses the https port: another port is not allowed.");
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new RejectedException(Problem.Invalid, "The address of a repository has no user name, password, query or fragment: a credential is named, never written in the address.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>A branch name Git accepts and that cannot be read as something else (an option, a path, a revision).</summary>
    internal static string? Branch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        var name = branch.Trim();
        if (name.Length > MaxBranchLength || name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.') || name.EndsWith(".lock", StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal) || name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c, StringComparison.Ordinal)))
        {
            throw new RejectedException(Problem.Invalid, "The name of the branch is not one that Git accepts.");
        }

        return name;
    }
}
