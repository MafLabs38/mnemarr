using LibGit2Sharp;

namespace Mnemarr.Core.Test.Git;

/// <summary>A repository on disk that plays the remote: commits are made in it, and a checkout is cloned from its path.</summary>
internal sealed class OriginRepository
{
    private static readonly Signature Author = new("Test", "test@homelab.example", DateTimeOffset.UnixEpoch);

    public OriginRepository(string folder)
    {
        Folder = folder;
        Repository.Init(folder);
    }

    public string Folder { get; }

    public string Branch
    {
        get
        {
            using var repository = new Repository(Folder);
            return repository.Head.FriendlyName;
        }
    }

    public string Commit(string path, string content)
    {
        using var repository = new Repository(Folder);
        TestWorkspace.Write(Folder, path, content);
        Commands.Stage(repository, path);
        return repository.Commit($"change {path}", Author, Author).Sha;
    }

    /// <summary>A branch of its own, with a commit that the main branch does not have; the repository is left on the main branch.</summary>
    public string CommitOnNewBranch(string name, string path, string content)
    {
        using var repository = new Repository(Folder);
        var main = repository.Head;
        Commands.Checkout(repository, repository.CreateBranch(name));
        TestWorkspace.Write(Folder, path, content);
        Commands.Stage(repository, path);
        var sha = repository.Commit($"change {path} on {name}", Author, Author).Sha;
        Commands.Checkout(repository, main);
        return sha;
    }

    public void CreateBranch(string name)
    {
        using var repository = new Repository(Folder);
        repository.CreateBranch(name);
    }

    /// <summary>Rewrites the history so that a checkout cloned before has diverged: the last commit is replaced by another one.</summary>
    public string Rewrite(string path, string content)
    {
        using var repository = new Repository(Folder);
        var parent = repository.Head.Tip.Parents.Single();
        repository.Reset(ResetMode.Hard, parent);
        TestWorkspace.Write(Folder, path, content);
        Commands.Stage(repository, path);
        return repository.Commit($"rewrite {path}", Author, Author).Sha;
    }
}
