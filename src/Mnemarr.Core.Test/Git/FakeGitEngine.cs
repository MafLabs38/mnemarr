using Mnemarr.Core.Git;

namespace Mnemarr.Core.Test.Git;

/// <summary>An engine that does what a test tells it, and remembers what it was asked.</summary>
internal sealed class FakeGitEngine : IGitEngine
{
    public List<(string Url, string? Branch, string Folder, GitSecret? Secret)> Calls { get; } = [];

    public Func<GitSyncResult> Result { get; set; } = () => new GitSyncResult(GitSyncAction.UpToDate, "abc123");

    /// <summary>Called inside the sync, to hold it while a test looks at what is going on around it.</summary>
    public Action? During { get; set; }

    public GitSyncResult Sync(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        Calls.Add((url, branch, folder, secret));
        During?.Invoke();
        return Result();
    }
}
