using LibGit2Sharp;
using Mnemarr.Core.Git;

namespace Mnemarr.Core.Test.Git;

public sealed class LibGit2EnginePublishFixture : IDisposable
{
    private static readonly GitAuthor Alice = new("alice", "mnemarr@localhost");
    private readonly TestWorkspace _workspace = new();
    private readonly LibGit2Engine _engine = new();
    private readonly string _remote;
    private readonly string _branch;

    public LibGit2EnginePublishFixture()
    {
        var seed = new OriginRepository(_workspace.Folder("seed"));
        seed.Commit("README.md", "# first");
        _branch = seed.Branch;
        _remote = Path.Combine(_workspace.SourcesFolder, "remote.git");
        // The remote is a bare repository, as a host's is: a push to the checked-out branch of a working repository is refused.
        Repository.Clone(seed.Folder, _remote, new CloneOptions { IsBare = true });
    }

    public void Dispose() => _workspace.Dispose();

    private string Clone(string name)
    {
        var folder = Path.Combine(_workspace.SourcesFolder, name);
        _engine.Sync(_remote, null, folder, null, CancellationToken.None);
        return folder;
    }

    private string RemoteTip()
    {
        using var repository = new Repository(_remote);
        return repository.Branches[_branch].Tip.Sha;
    }

    [Fact]
    public void should_commit_the_given_paths_as_the_author_and_push_them_so_that_the_remote_has_the_commit()
    {
        var folder = Clone("checkout");
        File.WriteAllText(Path.Combine(folder, "New.md"), "# new");

        var commit = _engine.Commit(folder, ["New.md"], "Add New.md", Alice);
        _engine.Push(_remote, null, folder, null, CancellationToken.None);

        commit.Should().NotBeNullOrEmpty();
        RemoteTip().Should().Be(commit);
        using var repository = new Repository(_remote);
        repository.Lookup<Commit>(commit).Should().Match<Commit>(c => c.Author.Name == "alice" && c.Author.Email == "mnemarr@localhost" && c.MessageShort == "Add New.md");
    }

    [Fact]
    public void should_commit_only_what_it_is_asked_to_and_leave_the_other_changes_of_the_folder_alone()
    {
        var folder = Clone("checkout");
        File.WriteAllText(Path.Combine(folder, "Mine.md"), "# mine, not committed");
        File.WriteAllText(Path.Combine(folder, "README.md"), "# edited by hand");
        File.WriteAllText(Path.Combine(folder, "New.md"), "# new");

        var commit = _engine.Commit(folder, ["New.md"], "Add New.md", Alice);

        using var repository = new Repository(folder);
        repository.Lookup<Commit>(commit).Tree.Select(entry => entry.Path).Should().BeEquivalentTo(["README.md", "New.md"]);
        repository.Lookup<Commit>(commit)["README.md"].Target.Peel<Blob>().GetContentText().Should().Be("# first", "a file that was not named is not in the commit");
        File.ReadAllText(Path.Combine(folder, "Mine.md")).Should().Be("# mine, not committed");
    }

    [Fact]
    public void should_commit_the_removal_of_a_file_and_the_move_of_a_file()
    {
        var folder = Clone("checkout");
        File.WriteAllText(Path.Combine(folder, "Old.md"), "# old");
        _engine.Commit(folder, ["Old.md"], "Add", Alice);
        File.Move(Path.Combine(folder, "Old.md"), Path.Combine(folder, "Moved.md"));
        File.Delete(Path.Combine(folder, "README.md"));

        var commit = _engine.Commit(folder, ["Old.md", "Moved.md", "README.md"], "Move and remove", Alice);

        using var repository = new Repository(folder);
        repository.Lookup<Commit>(commit).Tree.Select(entry => entry.Path).Should().BeEquivalentTo(["Moved.md"]);
    }

    [Fact]
    public void should_commit_nothing_when_the_paths_did_not_change()
    {
        var folder = Clone("checkout");

        _engine.Commit(folder, ["README.md"], "Nothing", Alice).Should().BeNull();
    }

    [Fact]
    public void should_push_nothing_when_the_branch_is_not_ahead()
    {
        var folder = Clone("checkout");
        var before = RemoteTip();

        _engine.Push(_remote, null, folder, null, CancellationToken.None);

        RemoteTip().Should().Be(before);
    }

    [Fact]
    public void should_push_a_commit_that_was_made_earlier_when_it_is_asked_again()
    {
        var folder = Clone("checkout");
        File.WriteAllText(Path.Combine(folder, "New.md"), "# new");
        var commit = _engine.Commit(folder, ["New.md"], "Add", Alice);

        _engine.Push(_remote, null, folder, null, CancellationToken.None);

        RemoteTip().Should().Be(commit);
    }

    [Fact]
    public void should_not_push_over_a_remote_that_moved_on_and_say_the_host_refused()
    {
        var first = Clone("first");
        var second = Clone("second");
        File.WriteAllText(Path.Combine(second, "Theirs.md"), "# theirs");
        _engine.Commit(second, ["Theirs.md"], "Theirs", Alice);
        _engine.Push(_remote, null, second, null, CancellationToken.None);
        var theirs = RemoteTip();
        File.WriteAllText(Path.Combine(first, "Mine.md"), "# mine");
        _engine.Commit(first, ["Mine.md"], "Mine", Alice);

        var failure = Assert.Throws<GitSyncException>(() => _engine.Push(_remote, null, first, null, CancellationToken.None));

        failure.Code.Should().Be(GitFailure.PushRejected);
        RemoteTip().Should().Be(theirs, "nothing was forced");
    }

    [Fact]
    public void should_report_the_checkout_as_diverged_when_it_has_a_commit_the_remote_does_not_and_the_remote_has_one_it_does_not()
    {
        var first = Clone("first");
        var second = Clone("second");
        File.WriteAllText(Path.Combine(second, "Theirs.md"), "# theirs");
        _engine.Commit(second, ["Theirs.md"], "Theirs", Alice);
        _engine.Push(_remote, null, second, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(first, "Mine.md"), "# mine");
        _engine.Commit(first, ["Mine.md"], "Mine", Alice);

        var failure = Assert.Throws<GitSyncException>(() => _engine.Sync(_remote, null, first, null, CancellationToken.None));

        failure.Code.Should().Be(GitFailure.Diverged);
        File.Exists(Path.Combine(first, "Mine.md")).Should().BeTrue();
    }
}
