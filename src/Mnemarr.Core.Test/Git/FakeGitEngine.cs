using Mnemarr.Core.Git;

namespace Mnemarr.Core.Test.Git;

/// <summary>An engine that does what a test tells it, and remembers what it was asked.</summary>
internal sealed class FakeGitEngine : IGitEngine
{
    public List<(string Url, string? Branch, string Folder, GitSecret? Secret)> Calls { get; } = [];

    public Func<GitSyncResult> Result { get; set; } = () => new GitSyncResult(GitSyncAction.UpToDate, "abc123");

    /// <summary>Called inside the sync, to hold it while a test looks at what is going on around it.</summary>
    public Action? During { get; set; }

    /// <summary>The engine waits for its token to be cancelled, as a host that answers slowly does, then stops.</summary>
    public bool Hang { get; set; }

    public List<(string Folder, string[] Paths, string Message, GitAuthor Author)> Commits { get; } = [];

    public int Pushes { get; set; }

    /// <summary>What a push does: nothing, or the failure a test asks for.</summary>
    public Action? OnPush { get; set; }

    public string? Commit(string folder, IReadOnlyCollection<string> paths, string message, GitAuthor author)
    {
        Commits.Add((folder, [.. paths], message, author));
        return "commit" + Commits.Count;
    }

    public void Push(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        Pushes++;
        OnPush?.Invoke();
    }

    public GitSyncResult Sync(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        Calls.Add((url, branch, folder, secret));
        During?.Invoke();
        if (Hang)
        {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Result();
    }
}
