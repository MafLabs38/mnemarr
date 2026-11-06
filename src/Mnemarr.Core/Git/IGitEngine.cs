namespace Mnemarr.Core.Git;

/// <summary>What a sync did.</summary>
public enum GitSyncAction
{
    /// <summary>The repository was not there: it was cloned.</summary>
    Cloned,

    /// <summary>New commits were brought in (a fast-forward).</summary>
    Updated,

    /// <summary>There was nothing new.</summary>
    UpToDate,
}

/// <summary>What a sync did and where the checkout is now: the commit its branch points at.</summary>
public sealed record GitSyncResult(GitSyncAction Action, string Head);

/// <summary>Why a sync failed, as a stable code that gives no address, no path and no token (shown on the source, and by readiness).</summary>
public static class GitFailure
{
    public const string AuthFailed = "auth-failed";
    public const string RepositoryNotFound = "repository-not-found";
    public const string Unreachable = "unreachable";
    public const string BranchNotFound = "branch-not-found";
    public const string Diverged = "diverged";
    public const string LocalChanges = "local-changes";
    public const string FolderNotEmpty = "folder-not-empty";
    public const string DifferentRepository = "different-repository";
    public const string CredentialMissing = "credential-missing";
    public const string OutsideRoots = "outside-roots";
    public const string HostNotAllowed = "host-not-allowed";
    public const string PushRejected = "push-rejected";
    public const string Failed = "sync-failed";
}

/// <summary>A sync that could not be done, with its code (<see cref="GitFailure"/>).</summary>
public sealed class GitSyncException : Exception
{
    public GitSyncException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Who a commit is by: the account that wrote the note, with an address that is not a person's.</summary>
public sealed record GitAuthor(string Name, string Email);

/// <summary>
/// Brings the checkout of a Git repository up to date: clones it into an empty folder, or fast-forwards it. Never forces: a checkout that has
/// diverged, or has changes, is reported and left as it is (D-35). The credential is sent to the host of the repository only.
/// </summary>
public interface IGitEngine
{
    /// <summary>
    /// Commits the given paths (a changed, added or removed file) and nothing else of what the folder holds: whatever else is there is not
    /// this commit's business. Returns the commit, or null when there was nothing to commit.
    /// </summary>
    string? Commit(string folder, IReadOnlyCollection<string> paths, string message, GitAuthor author);

    /// <summary>Pushes the branch to the remote when it is ahead of it (a fast-forward on the remote: never forced); nothing when it is not.</summary>
    /// <exception cref="GitSyncException">The push could not be done: see <see cref="GitFailure"/>.</exception>
    void Push(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken);

    /// <exception cref="GitSyncException">The sync could not be done: see <see cref="GitFailure"/>.</exception>
    GitSyncResult Sync(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken);
}
