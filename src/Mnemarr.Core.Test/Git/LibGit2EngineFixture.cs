using LibGit2Sharp;
using Mnemarr.Core.Git;

namespace Mnemarr.Core.Test.Git;

public sealed class LibGit2EngineFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly LibGit2Engine _engine = new();
    private readonly OriginRepository _origin;

    public LibGit2EngineFixture()
    {
        _origin = new OriginRepository(_workspace.Folder("origin"));
        _origin.Commit("README.md", "# first");
    }

    public void Dispose() => _workspace.Dispose();

    private string Checkout => Path.Combine(_workspace.SourcesFolder, "checkout");

    private GitSyncResult Sync(string? branch = null, string? url = null, string? folder = null) =>
        _engine.Sync(url ?? _origin.Folder, branch, folder ?? Checkout, null, CancellationToken.None);

    [Fact]
    public void should_clone_into_a_folder_that_is_not_there_yet()
    {
        var result = Sync();

        result.Action.Should().Be(GitSyncAction.Cloned);
        result.Head.Should().NotBeEmpty();
        File.ReadAllText(Path.Combine(Checkout, "README.md")).Should().Be("# first");
    }

    [Fact]
    public void should_clone_into_an_empty_folder()
    {
        Directory.CreateDirectory(Checkout);

        Sync().Action.Should().Be(GitSyncAction.Cloned);
    }

    [Fact]
    public void should_clone_the_branch_that_is_asked_for()
    {
        _origin.CreateBranch("drafts");

        using var repository = new Repository(Sync("drafts").Action == GitSyncAction.Cloned ? Checkout : throw new InvalidOperationException());

        repository.Head.FriendlyName.Should().Be("drafts");
    }

    [Fact]
    public void should_report_up_to_date_when_nothing_is_new()
    {
        var cloned = Sync();

        var again = Sync();

        again.Action.Should().Be(GitSyncAction.UpToDate);
        again.Head.Should().Be(cloned.Head);
    }

    [Fact]
    public void should_fast_forward_when_the_remote_has_new_commits()
    {
        Sync();
        var tip = _origin.Commit("Topic/new.md", "# new");

        var result = Sync();

        result.Action.Should().Be(GitSyncAction.Updated);
        result.Head.Should().Be(tip);
        File.ReadAllText(Path.Combine(Checkout, "Topic", "new.md")).Should().Be("# new");
    }

    [Fact]
    public void should_bring_in_a_removed_file_too()
    {
        Sync();
        _origin.Commit("Old.md", "# old");
        Sync();
        using (var origin = new Repository(_origin.Folder))
        {
            Commands.Remove(origin, "Old.md");
            var signature = new Signature("Test", "test@homelab.example", DateTimeOffset.UnixEpoch);
            origin.Commit("remove", signature, signature);
        }

        Sync().Action.Should().Be(GitSyncAction.Updated);

        File.Exists(Path.Combine(Checkout, "Old.md")).Should().BeFalse();
    }

    [Fact]
    public void should_refuse_a_diverged_checkout_and_leave_it_as_it_is()
    {
        _origin.Commit("Second.md", "# second");
        var cloned = Sync();
        _origin.Rewrite("Second.md", "# rewritten");

        var failure = Assert.Throws<GitSyncException>(() => Sync());

        failure.Code.Should().Be(GitFailure.Diverged);
        using var repository = new Repository(Checkout);
        repository.Head.Tip.Sha.Should().Be(cloned.Head);
        File.ReadAllText(Path.Combine(Checkout, "Second.md")).Should().Be("# second");
    }

    [Fact]
    public void should_refuse_a_checkout_with_changes_that_are_not_committed_and_keep_them()
    {
        Sync();
        _origin.Commit("Other.md", "# other");
        File.WriteAllText(Path.Combine(Checkout, "README.md"), "# edited by hand");

        var failure = Assert.Throws<GitSyncException>(() => Sync());

        failure.Code.Should().Be(GitFailure.LocalChanges);
        File.ReadAllText(Path.Combine(Checkout, "README.md")).Should().Be("# edited by hand");
        File.Exists(Path.Combine(Checkout, "Other.md")).Should().BeFalse();
    }

    [Fact]
    public void should_not_touch_a_checkout_that_is_ahead_of_the_remote()
    {
        Sync();
        using (var repository = new Repository(Checkout))
        {
            File.WriteAllText(Path.Combine(Checkout, "Local.md"), "# local");
            Commands.Stage(repository, "Local.md");
            var signature = new Signature("Test", "test@homelab.example", DateTimeOffset.UnixEpoch);
            repository.Commit("local", signature, signature);
        }

        Sync().Action.Should().Be(GitSyncAction.UpToDate);

        File.Exists(Path.Combine(Checkout, "Local.md")).Should().BeTrue();
    }

    [Fact]
    public void should_refuse_a_folder_that_holds_something_else_and_leave_it_alone()
    {
        TestWorkspace.Write(Checkout, "mine.md", "# mine");

        var failure = Assert.Throws<GitSyncException>(() => Sync());

        failure.Code.Should().Be(GitFailure.FolderNotEmpty);
        File.ReadAllText(Path.Combine(Checkout, "mine.md")).Should().Be("# mine");
    }

    [Fact]
    public void should_refuse_the_checkout_of_another_repository()
    {
        Sync();
        var other = new OriginRepository(_workspace.Folder("other"));
        other.Commit("README.md", "# other");

        var failure = Assert.Throws<GitSyncException>(() => Sync(url: other.Folder));

        failure.Code.Should().Be(GitFailure.DifferentRepository);
    }

    [Fact]
    public void should_report_a_branch_that_is_not_in_the_repository_on_a_clone_and_on_an_update()
    {
        Assert.Throws<GitSyncException>(() => Sync("nope")).Code.Should().Be(GitFailure.BranchNotFound);
        Directory.Exists(Checkout).Should().BeFalse("a clone that failed leaves nothing behind");

        Sync();
        Assert.Throws<GitSyncException>(() => Sync("nope")).Code.Should().Be(GitFailure.BranchNotFound);
    }

    [Fact]
    public void should_report_a_repository_that_is_not_there_and_leave_an_empty_folder_empty()
    {
        Directory.CreateDirectory(Checkout);

        var failure = Assert.Throws<GitSyncException>(() => Sync(url: Path.Combine(_workspace.Root, "missing")));

        failure.Code.Should().BeOneOf(GitFailure.RepositoryNotFound, GitFailure.Failed);
        Directory.EnumerateFileSystemEntries(Checkout).Should().BeEmpty();
    }

    [Fact]
    public void should_stop_when_it_is_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _engine.Sync(_origin.Folder, null, Checkout, null, cancelled.Token));
    }
}

public sealed class LibGit2EngineCodesFixture
{
    [Theory]
    [InlineData("authentication required but no callback set", GitFailure.AuthFailed)]
    [InlineData("request failed with status code: 401", GitFailure.AuthFailed)]
    [InlineData("request failed with status code: 403", GitFailure.AuthFailed)]
    [InlineData("unexpected http status code: 401 Unauthorized", GitFailure.AuthFailed)]
    [InlineData("remote branch nope not found in upstream origin", GitFailure.BranchNotFound)]
    [InlineData("reference 'refs/heads/x' not found", GitFailure.BranchNotFound)]
    [InlineData("unexpected http status code: 404", GitFailure.RepositoryNotFound)]
    [InlineData("repository does not exist", GitFailure.RepositoryNotFound)]
    [InlineData("failed to resolve address for host", GitFailure.Unreachable)]
    [InlineData("failed to connect to host: Connection refused", GitFailure.Unreachable)]
    [InlineData("the operation timed out", GitFailure.Unreachable)]
    [InlineData("SSL error: certificate verify failed", GitFailure.Unreachable)]
    [InlineData("something else that went wrong", GitFailure.Failed)]
    public void should_give_a_stable_code_from_what_libgit2_says(string message, string code)
    {
        LibGit2Engine.CodeOf(new LibGit2SharpException(message)).Should().Be(code);
    }

    [Fact]
    public void should_offer_the_token_once_to_the_address_of_the_repository_and_refuse_a_second_request_or_another_address()
    {
        var secret = new GitSecret("x-access-token", "tok");
        var offer = LibGit2Engine.OfferOnce("https://github.example/org/notes", secret);

        var first = offer("https://GitHub.example/org/notes", null, SupportedCredentialTypes.UsernamePassword);
        var second = () => offer("https://github.example/org/notes", null, SupportedCredentialTypes.UsernamePassword);
        var other = LibGit2Engine.OfferOnce("https://github.example/org/notes", secret);
        var elsewhere = () => other("https://elsewhere.example/org/notes", null, SupportedCredentialTypes.UsernamePassword);

        first.Should().BeOfType<UsernamePasswordCredentials>().Which.Should().Match<UsernamePasswordCredentials>(c => c.Username == "x-access-token" && c.Password == "tok");
        second.Should().Throw<GitSyncException>().Which.Code.Should().Be(GitFailure.AuthFailed);
        elsewhere.Should().Throw<GitSyncException>().Which.Code.Should().Be(GitFailure.AuthFailed);
    }
}
