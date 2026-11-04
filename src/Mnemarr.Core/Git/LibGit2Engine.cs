using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Mnemarr.Core.Git;

/// <summary>
/// <see cref="IGitEngine"/> over libgit2 (LibGit2Sharp, ADR 0022): in the process, with no <c>git</c> binary and no shell, so that nothing a
/// repository holds can be run (libgit2 runs no hook) and the image stays what it is. HTTPS with a token; no SSH.
/// </summary>
public sealed class LibGit2Engine : IGitEngine
{
    private const string Remote = "origin";

    public GitSyncResult Sync(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Repository.IsValid(folder) ? Update(url, branch, folder, secret, cancellationToken) : Clone(url, branch, folder, secret, cancellationToken);
        }
        catch (LibGit2SharpException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new GitSyncException(CodeOf(ex), "The repository could not be synchronised.", ex);
        }
    }

    private static GitSyncResult Clone(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        var created = !Directory.Exists(folder);
        if (!created && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new GitSyncException(GitFailure.FolderNotEmpty, "The folder of the source is not empty and is not a checkout: Mnemarr does not clone over what is there.");
        }

        Directory.CreateDirectory(folder);
        try
        {
            var options = new CloneOptions { BranchName = branch };
            Configure(options.FetchOptions, url, secret, cancellationToken);
            Repository.Clone(url, folder, options);
        }
        catch
        {
            // A clone that failed leaves nothing behind: the next one starts from an empty folder.
            Clean(folder, created);
            throw;
        }

        using var repository = new Repository(folder);
        return new GitSyncResult(GitSyncAction.Cloned, Tip(repository));
    }

    private static GitSyncResult Update(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        using var repository = new Repository(folder);
        if (!SameRepository(repository.Network.Remotes[Remote]?.Url, url))
        {
            throw new GitSyncException(GitFailure.DifferentRepository, "The folder of the source is the checkout of another repository.");
        }

        var options = new FetchOptions();
        Configure(options, url, secret, cancellationToken);
        Commands.Fetch(repository, Remote, [], options, null);

        var name = branch ?? repository.Head.FriendlyName;
        var remote = repository.Branches[$"{Remote}/{name}"]?.Tip ?? throw new GitSyncException(GitFailure.BranchNotFound, "The branch is not in the repository.");
        var switched = repository.Head.FriendlyName != name;
        if (switched)
        {
            // The source asks for another branch than the one that is checked out: move to it (a clean checkout only), then bring it up to date.
            SwitchTo(repository, name, remote);
        }

        var local = repository.Head.Tip;
        if (local is null || local.Id == remote.Id)
        {
            return new GitSyncResult(switched ? GitSyncAction.Updated : GitSyncAction.UpToDate, Tip(repository));
        }

        var common = repository.ObjectDatabase.FindMergeBase(local, remote);
        if (common?.Id == remote.Id)
        {
            // The checkout is ahead of the remote (a commit that was not pushed): nothing to bring in, and nothing to lose.
            return new GitSyncResult(GitSyncAction.UpToDate, Tip(repository));
        }

        if (common?.Id != local.Id)
        {
            throw new GitSyncException(GitFailure.Diverged, "The checkout and the remote branch have diverged: nothing was changed.");
        }

        if (repository.RetrieveStatus(new StatusOptions()).IsDirty)
        {
            throw new GitSyncException(GitFailure.LocalChanges, "The checkout has changes that are not committed: nothing was changed.");
        }

        // A fast-forward: the working tree and the branch move to the remote commit; nothing is discarded, as the checkout is clean and is behind.
        repository.Reset(ResetMode.Hard, remote);
        return new GitSyncResult(GitSyncAction.Updated, Tip(repository));
    }

    public string? Commit(string folder, IReadOnlyCollection<string> paths, string message, GitAuthor author)
    {
        using var repository = new Repository(folder);
        foreach (var path in paths)
        {
            Commands.Stage(repository, path);
        }

        const FileStatus staged = FileStatus.NewInIndex | FileStatus.ModifiedInIndex | FileStatus.DeletedFromIndex | FileStatus.RenamedInIndex | FileStatus.TypeChangeInIndex;
        if (!repository.RetrieveStatus(new StatusOptions()).Any(entry => (entry.State & staged) != 0))
        {
            return null;
        }

        var signature = new Signature(author.Name, author.Email, DateTimeOffset.UtcNow);
        return repository.Commit(message, signature, signature).Sha;
    }

    public void Push(string url, string? branch, string folder, GitSecret? secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var repository = new Repository(folder);
            var name = branch ?? repository.Head.FriendlyName;
            var local = repository.Branches[name]?.Tip ?? throw new GitSyncException(GitFailure.BranchNotFound, "The branch is not in the checkout.");
            var remote = repository.Branches[$"{Remote}/{name}"]?.Tip;
            if (remote is not null && (local.Id == remote.Id || repository.ObjectDatabase.FindMergeBase(local, remote)?.Id != remote.Id))
            {
                // Nothing ahead, or the remote has moved on (diverged): a fast-forward is the only push, and that one is not.
                if (local.Id != remote.Id)
                {
                    throw new GitSyncException(GitFailure.Diverged, "The checkout and the remote branch have diverged: nothing was pushed.");
                }

                return;
            }

            string? refused = null;
            // A host that accepts the connection and then stalls must not hold the caller (and the lock of the source) for ever: libgit2 stops when a callback says no.
            var options = new PushOptions
            {
                OnPushStatusError = error => refused = error.Message,
                OnPushTransferProgress = (_, _, _) => !cancellationToken.IsCancellationRequested,
                OnPackBuilderProgress = (_, _, _) => !cancellationToken.IsCancellationRequested,
            };
            if (secret is not null)
            {
                options.CredentialsProvider = OfferOnce(url, secret);
            }

            repository.Network.Push(repository.Network.Remotes[Remote], $"refs/heads/{name}:refs/heads/{name}", options);
            if (refused is not null)
            {
                throw new GitSyncException(GitFailure.PushRejected, "The host refused the push.");
            }
        }
        catch (LibGit2SharpException ex)
        {
            throw new GitSyncException(CodeOf(ex), "The push could not be done.", ex);
        }
    }

    private static void SwitchTo(Repository repository, string name, Commit remote)
    {
        if (repository.RetrieveStatus(new StatusOptions()).IsDirty)
        {
            throw new GitSyncException(GitFailure.LocalChanges, "The checkout has changes that are not committed: nothing was changed.");
        }

        Commands.Checkout(repository, repository.Branches[name] ?? repository.CreateBranch(name, remote));
    }

    private static void Configure(FetchOptions options, string url, GitSecret? secret, CancellationToken cancellationToken)
    {
        options.OnTransferProgress = _ => !cancellationToken.IsCancellationRequested;
        if (secret is null)
        {
            return;
        }

        options.CredentialsProvider = OfferOnce(url, secret);
    }

    /// <summary>The token goes to the address of the repository, and is offered once: a second request means it was refused, and must not be retried.</summary>
    internal static CredentialsHandler OfferOnce(string url, GitSecret secret)
    {
        var attempts = 0;
        return (requested, _, _) =>
            ++attempts == 1 && string.Equals(requested, url, StringComparison.OrdinalIgnoreCase)
                ? new UsernamePasswordCredentials { Username = secret.Username, Password = secret.Token }
                : throw new GitSyncException(GitFailure.AuthFailed, "The credential was refused.");
    }

    private static string Tip(Repository repository) => repository.Head.Tip?.Sha ?? string.Empty;

    private static bool SameRepository(string? existing, string url) =>
        existing is not null && string.Equals(existing.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static void Clean(string folder, bool created)
    {
        try
        {
            if (created)
            {
                Directory.Delete(folder, recursive: true);
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could not be removed is reported by the next sync (the folder is not empty): the first failure is the one to say.
        }
    }

    /// <summary>The code of a failure of libgit2, from what it says: it has no codes of its own for these, and the message is never shown.</summary>
    internal static string CodeOf(LibGit2SharpException exception)
    {
        var message = exception.Message.ToLowerInvariant();
        if (message.Contains("authentication", StringComparison.Ordinal) || message.Contains("credential", StringComparison.Ordinal) || message.Contains("401", StringComparison.Ordinal) || message.Contains("403", StringComparison.Ordinal) || message.Contains("unauthorized", StringComparison.Ordinal))
        {
            return GitFailure.AuthFailed;
        }

        if (message.Contains("cannot push", StringComparison.Ordinal) || message.Contains("non-fast-forward", StringComparison.Ordinal) || message.Contains("not present locally", StringComparison.Ordinal))
        {
            return GitFailure.PushRejected;
        }

        if (message.Contains("remote branch", StringComparison.Ordinal) || (message.Contains("reference", StringComparison.Ordinal) && message.Contains("not found", StringComparison.Ordinal)))
        {
            return GitFailure.BranchNotFound;
        }

        if (message.Contains("404", StringComparison.Ordinal) || message.Contains("not found", StringComparison.Ordinal) || message.Contains("repository does not exist", StringComparison.Ordinal))
        {
            return GitFailure.RepositoryNotFound;
        }

        return message.Contains("resolve", StringComparison.Ordinal) || message.Contains("connect", StringComparison.Ordinal) || message.Contains("timed out", StringComparison.Ordinal) || message.Contains("ssl", StringComparison.Ordinal)
            || message.Contains("certificate", StringComparison.Ordinal) || message.Contains("network", StringComparison.Ordinal)
            ? GitFailure.Unreachable
            : GitFailure.Failed;
    }
}
