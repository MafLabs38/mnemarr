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

    public GitSyncResult Sync(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add((url, branch, folder, secret));
        }

        return Result(url);
    }
}
