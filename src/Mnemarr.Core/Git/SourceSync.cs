using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Git;

/// <summary>What happened to a change once it was committed: pushed, or the fixed code of why it was not (the commit is kept, and is pushed by a later sync).</summary>
public sealed record GitPublish(bool Pushed, string? Error);

/// <summary>What a sync of a source did: how its checkout changed, where it is now, and what the pass over it that followed found.</summary>
public sealed record SourceSyncReport(GitSyncAction Action, string Head, IndexReport? Index);

/// <summary>
/// Brings the checkout of a Git source up to date (ADR 0022) and, when something came in, reads it (the indexer). The sync and the pass take the
/// lock of the source, so a pass never reads a folder that a checkout is changing. How it ended is kept for the source and for readiness, as a code.
/// </summary>
public sealed class SourceSync
{
    private readonly IGitEngine _engine;
    private readonly GitPolicy _policy;
    private readonly SourceIndexer _indexer;
    private readonly SourcePaths _paths;
    private readonly SourceLocks _locks;
    private readonly SyncOutcomes _outcomes;
    private readonly TimeProvider _clock;

    public SourceSync(IGitEngine engine, GitPolicy policy, SourceIndexer indexer, SourcePaths paths, SourceLocks locks, SyncOutcomes outcomes, TimeProvider clock)
    {
        _engine = engine;
        _policy = policy;
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
        string? pushFailure;
        try
        {
            // A host that answers slowly holds this source for this long and no longer: the next interval tries again.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_policy.SyncTimeout);
            (result, pushFailure) = await Task.Run(() => Run(source, repository, cancellationToken, timeout.Token), CancellationToken.None);
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

        if (pushFailure is not null)
        {
            // What was pulled is kept and read; what is waiting to leave is reported.
            Failed(source, pushFailure);
            throw new RejectedException(Problem.Conflict, MessageOf(pushFailure));
        }

        return new SourceSyncReport(result.Action, result.Head, index);
    }

    private (GitSyncResult Synced, string? PushFailure) Run(Source source, GitRepository repository, CancellationToken stopped, CancellationToken timedOut)
    {
        var folder = FolderOf(source);
        try
        {
            var url = GitPolicy.Address(repository.Url);
            // The operator's list of hosts is read again at each sync: narrowing it stops the sources that were declared for a host that is out.
            _policy.CheckHost(new Uri(url).Host);
            var secret = SecretFor(repository, url);
            var branch = GitPolicy.Branch(repository.Branch);
            var synced = _engine.Sync(url, branch, folder, secret, timedOut);
            if (source.Mode != SourceMode.ReadWrite)
            {
                return (synced, null);
            }

            try
            {
                // A commit that could not be pushed when it was made (the host was down) is pushed now.
                _engine.Push(url, branch, folder, secret, timedOut);
            }
            catch (GitSyncException ex)
            {
                // The pull has been done: it is not undone by a push that was refused.
                return (synced, ex.Code);
            }

            return (synced, null);
        }
        catch (OperationCanceledException) when (!stopped.IsCancellationRequested)
        {
            Failed(source, GitFailure.Unreachable);
            throw new RejectedException(Problem.Conflict, MessageOf(GitFailure.Unreachable));
        }
        catch (GitSyncException ex)
        {
            Failed(source, ex.Code);
            throw new RejectedException(Problem.Conflict, MessageOf(ex.Code));
        }
        catch (RejectedException)
        {
            // The address is not what the policy allows any more (the hosts were narrowed, a row was edited).
            Failed(source, GitFailure.HostNotAllowed);
            throw new RejectedException(Problem.Conflict, MessageOf(GitFailure.HostNotAllowed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else (the disk refused, the library failed in a way that has no code): said by a code, with nothing of the path or the message.
            Failed(source, GitFailure.Failed);
            throw new RejectedException(Problem.Conflict, MessageOf(GitFailure.Failed));
        }
    }

    /// <summary>
    /// Brings the checkout up to date before a note is written (D-35: write after bringing the working copy up to date), so that the push that
    /// follows is a fast-forward. The caller holds the lock of the source. A checkout that has diverged refuses the write (nothing is piled on a
    /// branch that cannot be pushed); a host that cannot be reached does not: the change is committed and pushed by a later sync.
    /// </summary>
    public async Task BringUpToDateAsync(Source source, CancellationToken cancellationToken)
    {
        var repository = source.Repository ?? throw new RejectedException(Problem.Invalid, "This source is a folder, not a Git repository.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_policy.WriteTimeout);
        GitSyncResult? synced = null;
        var folder = FolderOf(source);
        // What the operator configured is not forgiven: a host that was taken out of the list, a credential that is missing, stop the write here,
        // instead of leaving a note on disk that Publish will not commit (and that every later sync would then refuse as a local change).
        var url = GitPolicy.Address(repository.Url);
        _policy.CheckHost(new Uri(url).Host);
        var secret = SecretFor(repository, url);
        var branch = GitPolicy.Branch(repository.Branch);
        try
        {
            synced = await Task.Run(() => _engine.Sync(url, branch, folder, secret, timeout.Token), CancellationToken.None);
        }
        catch (GitSyncException ex) when (ex.Code == GitFailure.Diverged)
        {
            Failed(source, ex.Code);
            throw new RejectedException(Problem.Conflict, "The checkout and the remote branch have diverged: nothing was written. Bring the checkout back in line with the remote first.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The host is down, the token is refused, the address is not allowed any more: the write goes on, and its push says why it did not leave.
            synced = null;
            if (!Directory.Exists(Path.Combine(folder, ".git")) && !File.Exists(Path.Combine(folder, ".git")))
            {
                // Nothing was cloned yet (the host was down at the first sync): a note written into the empty folder would stop the clone for good.
                throw new RejectedException(Problem.Conflict, "This source has not been cloned yet: wait for its first sync.");
            }
        }

        if (synced is { Action: not GitSyncAction.UpToDate })
        {
            await _indexer.PassHeldAsync(source, rebuild: false, cancellationToken);
        }
    }

    /// <summary>
    /// Commits what a change of notes did to the checkout of a source, as the account that made it, and pushes it. The caller holds the lock of the
    /// source. A push that fails is not an error of the change: the note is written and committed, the failure is recorded as a code (on the
    /// source and in readiness), and the next sync pushes it.
    /// </summary>
    public GitPublish Publish(Source source, string message, string actor, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var repository = source.Repository ?? throw new RejectedException(Problem.Invalid, "This source is a folder, not a Git repository.");
        try
        {
            var folder = FolderOf(source);
            var url = GitPolicy.Address(repository.Url);
            _policy.CheckHost(new Uri(url).Host);
            var secret = SecretFor(repository, url);
            var author = new GitAuthor(string.IsNullOrWhiteSpace(actor) ? "mnemarr" : actor, _policy.CommitEmail);
            var committed = _engine.Commit(folder, paths, message, author);
            // A host that stalls holds the change (and the lock of the source) for the write timeout at most; the commit is kept and the next sync pushes it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_policy.WriteTimeout);
            _engine.Push(url, GitPolicy.Branch(repository.Branch), folder, secret, timeout.Token);
            // A push that leaves is a sync that worked: an earlier failure of this source is over.
            _outcomes.Succeeded(source.Id, _clock.GetUtcNow(), committed ?? string.Empty);
            return new GitPublish(true, null);
        }
        catch (GitSyncException ex)
        {
            Failed(source, ex.Code);
            return new GitPublish(false, ex.Code);
        }
        catch (RejectedException ex) when (ex.Problem != Problem.Busy)
        {
            Failed(source, GitFailure.HostNotAllowed);
            return new GitPublish(false, GitFailure.HostNotAllowed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(source, GitFailure.Failed);
            return new GitPublish(false, GitFailure.Failed);
        }
    }

    /// <summary>The folder of the source if it is still inside the roots; otherwise the sync stops, with its own code.</summary>
    private string FolderOf(Source source)
    {
        try
        {
            return _paths.AllowedFor(source);
        }
        catch (RejectedException)
        {
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
        return string.Equals(_policy.Credentials.HostOf(repository.Credential), host, StringComparison.OrdinalIgnoreCase) && _policy.Credentials.Secret(repository.Credential) is { } secret
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
        GitFailure.PushRejected => "The host refused the push: the branch may be protected, or the credential may not be allowed to write.",
        GitFailure.OutsideRoots => "The folder of the source is not inside the folders the operator allows for sources any more.",
        GitFailure.HostNotAllowed => "The address of the source is not what the operator allows for Git sources any more.",
        _ => "The repository could not be synchronised.",
    };
}
