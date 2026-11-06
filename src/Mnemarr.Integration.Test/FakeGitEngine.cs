using Mnemarr.Core.Git;

namespace Mnemarr.Integration.Test;

/// <summary>The Git engine of the test hosts: no network, no clone; it does what a test tells it and remembers what it was asked.</summary>
public sealed class FakeGitEngine : IGitEngine
{
    private readonly object _gate = new();
    private readonly List<(string Url, string? Branch, string Folder, GitSecret? Secret)> _calls = [];

    public Func<string, GitSyncResult> Result { get; set; } = _ => new GitSyncResult(GitSyncAction.Cloned, "abc123");

    public IReadOnlyList<(string Url, string? Branch, string Folder, GitSecret? Secret)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public List<(string Folder, string[] Paths, string Message, GitAuthor Author)> Commits { get; } = [];

    public int Pushes { get; private set; }

    public Action? OnPush { get; set; }

    public string? Commit(string folder, IReadOnlyCollection<string> paths, string message, GitAuthor author)
    {
        lock (_gate)
        {
            Commits.Add((folder, [.. paths], message, author));
            return "commit" + Commits.Count;
        }
    }

    public void Push(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Pushes++;
        }

        OnPush?.Invoke();
    }

    public GitSyncResult Sync(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add((url, branch, folder, secret));
        }

        return Result(url);
    }
}
