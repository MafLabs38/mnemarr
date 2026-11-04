using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Git;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Sources;

public sealed class SourceCatalogFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly SqliteSourceStore _store;
    private readonly SqliteAuditLog _audit;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    public SourceCatalogFixture()
    {
        _store = new SqliteSourceStore(_workspace.Database);
        _audit = new SqliteAuditLog(_workspace.Database, _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private SourceCatalog Catalog(params string[] roots) =>
        new(_store, _audit, _workspace.Paths(roots), _clock);

    private SourceCatalog GitCatalog(GitOptions? options = null)
    {
        var wrapped = Options.Create(options ?? new GitOptions { Hosts = ["github.example"], Credentials = { ["github"] = new GitCredentialOptions { Host = "github.example", Token = "secret-token" } } });
        return new SourceCatalog(_store, _audit, _workspace.Paths(), _clock, new GitPolicy(wrapped, new GitCredentials(wrapped)));
    }

    [Fact]
    public void should_declare_a_git_source_in_a_folder_that_does_not_exist_yet_and_keep_the_repository_in_its_normal_form()
    {
        var folder = Path.Combine(_workspace.SourcesFolder, "notes");

        var source = GitCatalog().Add("notes", SourceKind.Knowledge, SourceMode.ReadOnly, folder, SourceScope.All, "root", new GitRepository("https://GitHub.example/org/notes/", "main", "github"));

        source.Repository.Should().Be(new GitRepository("https://github.example/org/notes", "main", "github"));
        source.Path.Should().Be(folder);
        _store.Get(source.Id)!.Repository.Should().Be(source.Repository);
        Directory.Exists(folder).Should().BeFalse("the clone creates it");
    }

    [Fact]
    public void should_write_the_address_but_never_a_credential_in_the_audit_trail()
    {
        GitCatalog().Add("notes", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(_workspace.SourcesFolder, "notes"), SourceScope.All, "root", new GitRepository("https://github.example/org/notes", null, "github"));

        var events = _audit.Recent(10, "source.added");

        events.Should().ContainSingle().Which.Detail.Should().Contain("https://github.example/org/notes");
        events.Single().Detail.Should().NotContain("secret-token");
    }

    [Fact]
    public void should_refuse_a_git_source_when_git_is_not_available_or_the_policy_refuses_it()
    {
        var folder = Path.Combine(_workspace.SourcesFolder, "notes");

        Catalog().Invoking(c => c.Add("a", SourceKind.Knowledge, SourceMode.ReadOnly, folder, SourceScope.All, "root", new GitRepository("https://github.example/org/notes")))
            .Should().Throw<RejectedException>().WithMessage("*not available*");
        GitCatalog().Invoking(c => c.Add("b", SourceKind.Knowledge, SourceMode.ReadOnly, folder, SourceScope.All, "root", new GitRepository("https://elsewhere.example/org/notes")))
            .Should().Throw<RejectedException>().WithMessage("*does not allow*");
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public void should_not_let_a_git_source_be_cloned_outside_the_roots()
    {
        GitCatalog().Invoking(c => c.Add("a", SourceKind.Knowledge, SourceMode.ReadOnly, "/etc/notes", SourceScope.All, "root", new GitRepository("https://github.example/org/notes")))
            .Should().Throw<RejectedException>().WithMessage("*inside one of the folders*");
    }

    [Fact]
    public void should_change_the_repository_of_a_source_and_turn_a_git_source_back_into_a_folder()
    {
        var catalog = GitCatalog();
        var folder = _workspace.Folder("notes");
        var source = catalog.Add("notes", SourceKind.Knowledge, SourceMode.ReadOnly, folder, SourceScope.All, "root", new GitRepository("https://github.example/org/notes", "main", null));

        var moved = catalog.Update(source.Id, "notes", SourceMode.ReadOnly, folder, SourceScope.All, "root", new GitRepository("https://github.example/org/other", "dev", "github"));
        var plain = catalog.Update(source.Id, "notes", SourceMode.ReadOnly, folder, SourceScope.All, "root");

        moved.Repository.Should().Be(new GitRepository("https://github.example/org/other", "dev", "github"));
        plain.Repository.Should().BeNull();
        _store.Get(source.Id)!.Repository.Should().BeNull();
    }

    [Fact]
    public void should_not_clone_into_the_folder_of_another_source_nor_around_one()
    {
        var catalog = GitCatalog();
        var memory = _workspace.Folder("memory");
        catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, memory, SourceScope.All, "root");
        var repository = new GitRepository("https://github.example/org/notes");

        var inside = () => catalog.Add("a", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(memory, "imported"), SourceScope.All, "root", repository);
        var same = () => catalog.Add("b", SourceKind.Knowledge, SourceMode.ReadOnly, memory, SourceScope.All, "root", repository);
        var around = () => catalog.Add("c", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.SourcesFolder, SourceScope.All, "root", repository);
        var beside = catalog.Add("d", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(_workspace.SourcesFolder, "memory-notes"), SourceScope.All, "root", repository);

        inside.Should().Throw<RejectedException>().WithMessage("*another source*");
        same.Should().Throw<RejectedException>();
        around.Should().Throw<RejectedException>();
        beside.Repository.Should().NotBeNull("a folder that only starts with the same letters is not inside");
    }

    [Fact]
    public void should_let_a_git_source_keep_its_own_folder_when_it_is_changed()
    {
        var catalog = GitCatalog();
        var folder = Path.Combine(_workspace.SourcesFolder, "notes");
        var source = catalog.Add("notes", SourceKind.Knowledge, SourceMode.ReadOnly, folder, SourceScope.All, "root", new GitRepository("https://github.example/org/notes"));

        var changed = catalog.Update(source.Id, "notes", SourceMode.ReadWrite, folder, SourceScope.All, "root", new GitRepository("https://github.example/org/notes", "dev", null));

        changed.Mode.Should().Be(SourceMode.ReadWrite);
    }

    [Fact]
    public void should_see_the_same_repository_typed_another_way_as_no_change_for_a_key_limited_to_some_sources()
    {
        var catalog = GitCatalog();
        var folder = _workspace.Folder("notes");
        var source = catalog.Add("notes", SourceKind.Knowledge, SourceMode.ReadOnly, folder, SourceScope.All, "root", new GitRepository("https://github.example/org/notes", "main", null));

        var changed = catalog.Update(source.Id, "notes", SourceMode.ReadWrite, folder, new SourceScope([source.Id]), "key", new GitRepository("https://GitHub.example/org/notes/", " main ", " "));

        changed.Mode.Should().Be(SourceMode.ReadWrite);
    }

    [Fact]
    public void should_let_a_key_limited_to_some_sources_change_the_mode_only_not_the_repository()
    {
        var catalog = GitCatalog();
        var folder = _workspace.Folder("notes");
        var source = catalog.Add("notes", SourceKind.Knowledge, SourceMode.ReadOnly, folder, SourceScope.All, "root", new GitRepository("https://github.example/org/notes", "main", null));
        var scope = new SourceScope([source.Id]);

        var sameButMode = catalog.Update(source.Id, "notes", SourceMode.ReadWrite, folder, scope, "key", new GitRepository("https://github.example/org/notes", "main", null));
        var elsewhere = () => catalog.Update(source.Id, "notes", SourceMode.ReadWrite, folder, scope, "key", new GitRepository("https://github.example/org/elsewhere", "main", null));
        var dropped = () => catalog.Update(source.Id, "notes", SourceMode.ReadWrite, folder, scope, "key");

        sameButMode.Mode.Should().Be(SourceMode.ReadWrite);
        sameButMode.Repository.Should().Be(source.Repository);
        elsewhere.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Forbidden);
        dropped.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Forbidden);
    }

    [Fact]
    public void should_declare_a_folder_of_the_sources_folder_by_default()
    {
        var folder = _workspace.Folder("courses");

        var source = Catalog().Add("  Courses  ", SourceKind.Knowledge, SourceMode.ReadWrite, folder + Path.DirectorySeparatorChar, SourceScope.All, "root");

        source.Should().Match<Source>(s => s.Name == "Courses" && s.Path == folder && s.Kind == SourceKind.Knowledge && s.Mode == SourceMode.ReadWrite);
        _store.Get(source.Id).Should().Be(source);
        _store.List().Should().ContainSingle().Which.Should().Be(source);
    }

    [Fact]
    public void should_accept_a_folder_below_a_root_the_operator_chose()
    {
        using var vaults = new TempFolder();
        var vault = Directory.CreateDirectory(Path.Combine(vaults.Path, "memory")).FullName;

        Catalog(vaults.Path).Add("memory", SourceKind.Memory, SourceMode.ReadWrite, vault, SourceScope.All, "root").Path.Should().Be(vault);
        Catalog(vaults.Path).Roots().Should().Equal(vaults.Path);
    }

    [Theory]
    [InlineData("relative/path", "absolute")]
    [InlineData("/tmp/with\0nul", "absolute")]
    [InlineData("", "absolute")]
    [InlineData("/etc", "inside one of the folders")]
    public void should_refuse_a_path_that_is_not_absolute_or_not_allowed(string path, string reason)
    {
        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, path, SourceScope.All, "root");

        add.Should().Throw<RejectedException>().WithMessage($"*{reason}*");
    }

    [Fact]
    public void should_refuse_a_key_limited_to_some_sources_that_forgets_one_or_changes_it_with_a_path_that_is_not_one()
    {
        var catalog = Catalog();
        var source = catalog.Add("mine", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("mine"), SourceScope.All, "root");
        var scope = new SourceScope([source.Id]);

        var forget = () => catalog.Remove(source.Id, scope, "key");
        var repoint = () => catalog.Update(source.Id, "mine", SourceMode.ReadOnly, "/tmp/with\0nul", scope, "key");

        forget.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Forbidden);
        repoint.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid, "a path that cannot be one is a bad request, not a failure");
        catalog.List(SourceScope.All).Should().ContainSingle();
    }

    [Fact]
    public void should_refuse_a_path_that_climbs_out_of_the_allowed_folder()
    {
        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(_workspace.SourcesFolder, "..", "data-other"), SourceScope.All, "root");

        add.Should().Throw<RejectedException>().WithMessage("*inside one of the folders*");
    }

    [Fact]
    public void should_refuse_a_sibling_whose_name_starts_like_the_root()
    {
        var sibling = Directory.CreateDirectory(_workspace.SourcesFolder + "-evil").FullName;

        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, sibling, SourceScope.All, "root");

        add.Should().Throw<RejectedException>().WithMessage("*inside one of the folders*");
    }

    [Fact]
    public void should_refuse_a_folder_that_does_not_exist()
    {
        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(_workspace.SourcesFolder, "nothing"), SourceScope.All, "root");

        add.Should().Throw<RejectedException>().WithMessage("*does not exist*");
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void should_refuse_a_folder_that_is_a_link()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creating a link needs no privilege on Linux only.");
        using var outside = new TempFolder();
        var link = Path.Combine(_workspace.SourcesFolder, "link");
        Directory.CreateSymbolicLink(link, outside.Path);

        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, link, SourceScope.All, "root");

        add.Should().Throw<RejectedException>().WithMessage("*link*");
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void should_refuse_a_folder_below_a_link_even_when_the_folder_itself_is_not_one()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creating a link needs no privilege on Linux only.");
        using var outside = new TempFolder();
        Directory.CreateDirectory(Path.Combine(outside.Path, "deep", "vault"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace.SourcesFolder, "esc"), outside.Path);

        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(_workspace.SourcesFolder, "esc", "deep", "vault"), SourceScope.All, "root");

        add.Should().Throw<RejectedException>().WithMessage("*link*");
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void should_accept_a_root_that_is_itself_a_link_because_the_operator_chose_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creating a link needs no privilege on Linux only.");
        using var real = new TempFolder();
        Directory.CreateDirectory(Path.Combine(real.Path, "vault"));
        var rootLink = Path.Combine(_workspace.Root, "root-link");
        Directory.CreateSymbolicLink(rootLink, real.Path);

        var source = Catalog(rootLink).Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(rootLink, "vault"), SourceScope.All, "root");

        source.Path.Should().Be(Path.Combine(rootLink, "vault"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    [InlineData("nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn")]
    public void should_refuse_a_source_whose_name_is_empty_too_long_or_holds_a_control_character(string name)
    {
        var add = () => Catalog().Add(name, SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), SourceScope.All, "root");

        add.Should().Throw<RejectedException>().WithMessage("*name*");
    }

    [Fact]
    public void should_refuse_a_name_that_is_already_taken_and_keep_the_first_source()
    {
        var catalog = Catalog();
        catalog.Add("courses", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("a"), SourceScope.All, "root");

        var again = () => catalog.Add("courses", SourceKind.Memory, SourceMode.ReadOnly, _workspace.Folder("b"), SourceScope.All, "root");

        again.Should().Throw<RejectedException>().WithMessage("*already exists*");
        _store.List().Should().ContainSingle().Which.Kind.Should().Be(SourceKind.Knowledge);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("mnemarr-roots-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void should_audit_what_is_declared_and_forgotten_and_say_so_when_a_source_is_not_there()
    {
        var catalog = Catalog();
        var source = catalog.Add("courses", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("a"), SourceScope.All, "alice");

        catalog.Remove(source.Id, SourceScope.All, "bob");

        _audit.Recent(10, "source.added").Should().ContainSingle().Which.Should().Match<AuditEvent>(e => e.Actor == "alice" && e.Target == "courses");
        _audit.Recent(10, "source.removed").Should().ContainSingle().Which.Actor.Should().Be("bob");
        _store.List().Should().BeEmpty();
        var again = () => catalog.Remove(source.Id, SourceScope.All, "bob");
        again.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public void should_show_a_caller_limited_to_some_sources_those_and_treat_the_others_as_absent()
    {
        var catalog = Catalog();
        var mine = catalog.Add("mine", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), SourceScope.All, "root");
        var other = catalog.Add("other", SourceKind.Memory, SourceMode.ReadOnly, _workspace.Folder("b"), SourceScope.All, "root");
        var scope = new SourceScope([mine.Id]);

        catalog.List(scope).Should().ContainSingle().Which.Name.Should().Be("mine");
        catalog.List(new SourceScope([])).Should().BeEmpty();
        var get = () => catalog.Get(other.Id, scope);
        get.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
        var remove = () => catalog.Remove(other.Id, scope, "x");
        remove.Should().Throw<RejectedException>();
        _store.Get(other.Id).Should().NotBeNull();
    }

    [Fact]
    public void should_declare_only_one_memory_vault_and_as_many_knowledge_vaults_and_application_docs_as_there_are()
    {
        var catalog = Catalog();
        catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), SourceScope.All, "root");
        catalog.Add("courses", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("k1"), SourceScope.All, "root");
        catalog.Add("work", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("k2"), SourceScope.All, "root");
        catalog.Add("app-a", SourceKind.ApplicationDocs, SourceMode.ReadOnly, _workspace.Folder("a1"), SourceScope.All, "root");
        catalog.Add("app-b", SourceKind.ApplicationDocs, SourceMode.ReadOnly, _workspace.Folder("a2"), SourceScope.All, "root");

        var second = () => catalog.Add("memory-2", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m2"), SourceScope.All, "root");

        second.Should().Throw<RejectedException>().Which.Should().Match<RejectedException>(e => e.Problem == Problem.Conflict && e.Message.Contains("only 1") && !e.Message.Contains("memory"));
        _store.List().Should().HaveCount(5);
    }

    [Fact]
    public void should_say_for_each_kind_how_many_are_declared_and_whether_the_base_is_complete()
    {
        var catalog = Catalog();

        var before = catalog.Kinds();
        catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), SourceScope.All, "root");
        var after = catalog.Kinds();

        before.Single(k => k.Rule.Kind == SourceKind.Memory).Should().Match<KindStatus>(k => k.Count == 0 && !k.Satisfied && k.Rule.Required && k.Rule.Unique);
        before.Where(k => k.Rule.Kind != SourceKind.Memory).Should().OnlyContain(k => k.Satisfied && !k.Rule.Required);
        after.Single(k => k.Rule.Kind == SourceKind.Memory).Should().Match<KindStatus>(k => k.Count == 1 && k.Satisfied);
    }

    [Fact]
    public void should_not_let_the_required_memory_vault_be_forgotten_but_let_the_optional_ones_go()
    {
        var catalog = Catalog();
        var memory = catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), SourceScope.All, "root");
        var cours = catalog.Add("courses", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("k"), SourceScope.All, "root");

        var forget = () => catalog.Remove(memory.Id, SourceScope.All, "root");
        catalog.Remove(cours.Id, SourceScope.All, "root");

        forget.Should().Throw<RejectedException>().Which.Should().Match<RejectedException>(e => e.Problem == Problem.Conflict && e.Message.Contains("required"));
        _store.List().Should().ContainSingle().Which.Id.Should().Be(memory.Id);
    }

    [Fact]
    public void should_change_the_folder_the_mode_and_the_name_of_a_source_but_never_its_kind()
    {
        var catalog = Catalog();
        var memory = catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), SourceScope.All, "root");
        var other = _workspace.Folder("m-new");

        var changed = catalog.Update(memory.Id, " memoire ", SourceMode.ReadOnly, other, SourceScope.All, "alice");

        changed.Should().Match<Source>(s => s.Id == memory.Id && s.Name == "memoire" && s.Mode == SourceMode.ReadOnly && s.Path == other && s.Kind == SourceKind.Memory);
        _store.Get(memory.Id).Should().Be(changed);
        _audit.Recent(10, "source.changed").Should().ContainSingle().Which.Actor.Should().Be("alice");
    }

    [Fact]
    public void should_refuse_a_change_that_leaves_the_allowed_roots_takes_a_name_or_targets_a_source_out_of_scope_and_change_nothing()
    {
        var catalog = Catalog();
        var one = catalog.Add("one", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("a"), SourceScope.All, "root");
        var two = catalog.Add("two", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("b"), SourceScope.All, "root");

        var outside = () => catalog.Update(one.Id, "one", SourceMode.ReadWrite, "/etc", SourceScope.All, "x");
        var taken = () => catalog.Update(one.Id, "two", SourceMode.ReadWrite, one.Path, SourceScope.All, "x");
        var hidden = () => catalog.Update(two.Id, "two", SourceMode.ReadWrite, two.Path, new SourceScope([one.Id]), "x");

        outside.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
        taken.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Conflict);
        hidden.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
        _store.List().Select(s => s.Name).Should().Equal("one", "two");
    }

    [Fact]
    public void should_not_let_a_caller_limited_to_some_sources_declare_a_source_or_repoint_one()
    {
        var catalog = Catalog();
        var mine = catalog.Add("mine", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), SourceScope.All, "root");
        var other = catalog.Add("other", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("b"), SourceScope.All, "root");
        var scope = new SourceScope([mine.Id]);

        var declare = () => catalog.Add("new", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("c"), scope, "key");
        var repoint = () => catalog.Update(mine.Id, "mine", SourceMode.ReadOnly, other.Path, scope, "key");
        var rename = () => catalog.Update(mine.Id, "other", SourceMode.ReadOnly, mine.Path, scope, "key");
        var toTheRoot = () => catalog.Update(mine.Id, "mine", SourceMode.ReadOnly, _workspace.SourcesFolder, scope, "key");

        declare.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Forbidden);
        repoint.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Forbidden);
        rename.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Forbidden);
        toTheRoot.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Forbidden);
        _store.List().Select(s => (s.Name, s.Path)).Should().BeEquivalentTo([("mine", mine.Path), ("other", other.Path)]);
    }

    [Fact]
    public void should_let_a_caller_limited_to_some_sources_change_the_mode_of_its_own_source()
    {
        var catalog = Catalog();
        var mine = catalog.Add("mine", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), SourceScope.All, "root");

        var changed = catalog.Update(mine.Id, "mine", SourceMode.ReadWrite, mine.Path, new SourceScope([mine.Id]), "key");

        changed.Mode.Should().Be(SourceMode.ReadWrite);
    }

    [Fact]
    public void should_count_only_the_sources_a_caller_can_see_in_the_kinds()
    {
        var catalog = Catalog();
        var mine = catalog.Add("mine", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), SourceScope.All, "root");
        catalog.Add("memory", SourceKind.Memory, SourceMode.ReadOnly, _workspace.Folder("m"), SourceScope.All, "root");

        var seen = catalog.Kinds(new SourceScope([mine.Id]));

        seen.Single(k => k.Rule.Kind == SourceKind.Knowledge).Count.Should().Be(1);
        seen.Single(k => k.Rule.Kind == SourceKind.Memory).Count.Should().Be(0);
        catalog.Kinds().Single(k => k.Rule.Kind == SourceKind.Memory).Count.Should().Be(1);
    }

    [Fact]
    public void should_refuse_a_scoped_caller_a_new_folder_before_looking_at_the_disk_whatever_the_folder()
    {
        var catalog = Catalog();
        var mine = catalog.Add("mine", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), SourceScope.All, "root");
        var scope = new SourceScope([mine.Id]);

        var answers = new[] { "/etc", Path.Combine(_workspace.SourcesFolder, "nothing"), _workspace.Folder("exists") }
            .Select(path => Record.Exception(() => catalog.Update(mine.Id, "mine", SourceMode.ReadOnly, path, scope, "key")))
            .Select(ex => ((RejectedException)ex!).Message)
            .Distinct();

        answers.Should().ContainSingle("the answer must not tell what exists under the roots");
    }
}
