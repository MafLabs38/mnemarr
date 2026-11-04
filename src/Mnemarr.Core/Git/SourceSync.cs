using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Git;

/// <summary>What a sync of a source did: how its checkout changed, where it is now, and what the pass over it that followed found.</summary>
public sealed record SourceSyncReport(GitSyncAction Action, string Head, IndexReport? Index);

/// <summary>
/// Brings the checkout of a Git source up to date (ADR 0022) and, when something came in, reads it (the indexer). The sync and the pass take the
/// lock of the source, so a pass never reads a folder that a checkout is changing. How it ended is kept for the source and for readiness, as a code.
/// </summary>
public sealed class SourceSync
{
    private readonly IGitEngine _engine;
    private readonly GitCredentials _credentials;
    private readonly SourceIndexer _indexer;
    private readonly SourcePaths _paths;
    private readonly SourceLocks _locks;
    private readonly SyncOutcomes _outcomes;
    private readonly TimeProvider _clock;

    public SourceSync(IGitEngine engine, GitCredentials credentials, SourceIndexer indexer, SourcePaths paths, SourceLocks locks, SyncOutcomes outcomes, TimeProvider clock)
    {
        _engine = engine;
        _credentials = credentials;
        _indexer = indexer;
        _paths = paths;
        _locks = locks;
        _outcomes = outcomes;
        _clock = clock;
    }

    /// <param name="source">A source that is the checkout of a repository.</param>
    /// <param name="waitForTurn">Wait for the sync or the pass that is running (the background service does); otherwise refuse with <see cref="Problem.Busy"/>.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    public async Task<SourceSyncReport> SyncAsync(Source source, bool waitForTurn, CancellationToken cancellationToken)
    {
        var repository = source.Repository ?? throw new RejectedException(Problem.Invalid, "This source is a folder, not a Git repository.");
        var gate = _locks.Of(source.Id);
        if (!await gate.WaitAsync(waitForTurn ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, cancellationToken))
        {
            throw new RejectedException(Problem.Busy, "This source is being synchronised or read: try again in a moment.", TimeSpan.FromSeconds(5));
        }

        GitSyncResult result;
        try
        {
            result = await Task.Run(() => Run(source, repository, cancellationToken), cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        _outcomes.Succeeded(source.Id, _clock.GetUtcNow(), result.Head);
        // What came in is read at once, instead of at the next scan; a pass that fails says so by itself, and does not undo the sync.
        IndexReport? index = null;
        if (result.Action != GitSyncAction.UpToDate)
        {
            index = await _indexer.IndexAsync(source, cancellationToken);
        }

        return new SourceSyncReport(result.Action, result.Head, index);
    }

    private GitSyncResult Run(Source source, GitRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var folder = _paths.Allowed(source.Path);
            var url = GitPolicy.Address(repository.Url);
            var secret = SecretFor(repository, url);
            return _engine.Sync(url, GitPolicy.Branch(repository.Branch), folder, secret, cancellationToken);
        }
        catch (GitSyncException ex)
        {
            Failed(source, ex.Code);
            throw new RejectedException(Problem.Conflict, MessageOf(ex.Code));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The disk refused (a folder that cannot be written): said by its code, with nothing of the path.
            Failed(source, GitFailure.Failed);
            throw new RejectedException(Problem.Conflict, MessageOf(GitFailure.Failed));
        }
        catch (RejectedException)
        {
            // The folder or the address is not what the policy allows any more (the roots were narrowed, a row was edited).
            Failed(source, GitFailure.OutsideRoots);
            throw new RejectedException(Problem.Conflict, MessageOf(GitFailure.OutsideRoots));
        }
    }

    private GitSecret? SecretFor(GitRepository repository, string url)
    {
        if (repository.Credential is null)
        {
            return null;
        }

        // A credential is sent to its own host and to no other, whatever the source says: checked here again, not only when the source was declared.
        var host = new Uri(url).Host;
        return string.Equals(_credentials.HostOf(repository.Credential), host, StringComparison.OrdinalIgnoreCase) && _credentials.Secret(repository.Credential) is { } secret
            ? secret
            : throw new GitSyncException(GitFailure.CredentialMissing, MessageOf(GitFailure.CredentialMissing));
    }

    private void Failed(Source source, string code) => _outcomes.Failed(source.Id, _clock.GetUtcNow(), code);

    /// <summary>What to tell a person about a failure: fixed words for each code, nothing from the host or from the disk.</summary>
    public static string MessageOf(string code) => code switch
    {
        GitFailure.AuthFailed => "The host refused the credential, or the repository needs one.",
        GitFailure.RepositoryNotFound => "The repository was not found (or the credential may not see it).",
        GitFailure.Unreachable => "The host could not be reached.",
        GitFailure.BranchNotFound => "The branch is not in the repository.",
        GitFailure.Diverged => "The checkout and the remote branch have diverged: nothing was changed.",
        GitFailure.LocalChanges => "The checkout has changes that are not committed: nothing was changed.",
        GitFailure.FolderNotEmpty => "The folder of the source is not empty and is not a checkout: Mnemarr does not clone over what is there.",
        GitFailure.DifferentRepository => "The folder of the source is the checkout of another repository.",
        GitFailure.CredentialMissing => "The credential of the source is not there, or its token cannot be read.",
        GitFailure.OutsideRoots => "The folder or the address of the source is not what the operator allows for sources any more.",
        _ => "The repository could not be synchronised.",
    };
}
