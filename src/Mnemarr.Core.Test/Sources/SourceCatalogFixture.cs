using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
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
        new(_store, _audit, _workspace.Options, Options.Create(new SourcesOptions { Roots = roots }), _clock);

    [Fact]
    public void should_declare_a_folder_of_the_sources_folder_by_default()
    {
        var folder = _workspace.Folder("cours");

        var source = Catalog().Add("  Cours  ", SourceKind.Knowledge, SourceMode.ReadWrite, folder + Path.DirectorySeparatorChar, "root");

        source.Should().Match<Source>(s => s.Name == "Cours" && s.Path == folder && s.Kind == SourceKind.Knowledge && s.Mode == SourceMode.ReadWrite);
        _store.Get(source.Id).Should().Be(source);
        _store.List().Should().ContainSingle().Which.Should().Be(source);
    }

    [Fact]
    public void should_accept_a_folder_below_a_root_the_operator_chose()
    {
        using var vaults = new TempFolder();
        var vault = Directory.CreateDirectory(Path.Combine(vaults.Path, "memory")).FullName;

        Catalog(vaults.Path).Add("memory", SourceKind.Memory, SourceMode.ReadWrite, vault, "root").Path.Should().Be(vault);
        Catalog(vaults.Path).Roots().Should().Equal(vaults.Path);
    }

    [Theory]
    [InlineData("relative/path", "absolute")]
    [InlineData("/etc", "inside one of the folders")]
    public void should_refuse_a_path_that_is_not_absolute_or_not_allowed(string path, string reason)
    {
        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, path, "root");

        add.Should().Throw<RejectedException>().WithMessage($"*{reason}*");
    }

    [Fact]
    public void should_refuse_a_path_that_climbs_out_of_the_allowed_folder()
    {
        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(_workspace.SourcesFolder, "..", "data-other"), "root");

        add.Should().Throw<RejectedException>().WithMessage("*inside one of the folders*");
    }

    [Fact]
    public void should_refuse_a_sibling_whose_name_starts_like_the_root()
    {
        var sibling = Directory.CreateDirectory(_workspace.SourcesFolder + "-evil").FullName;

        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, sibling, "root");

        add.Should().Throw<RejectedException>().WithMessage("*inside one of the folders*");
    }

    [Fact]
    public void should_refuse_a_folder_that_does_not_exist()
    {
        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, Path.Combine(_workspace.SourcesFolder, "nothing"), "root");

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

        var add = () => Catalog().Add("x", SourceKind.Knowledge, SourceMode.ReadOnly, link, "root");

        add.Should().Throw<RejectedException>().WithMessage("*cannot be a link*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void should_refuse_a_source_without_a_name(string name)
    {
        var add = () => Catalog().Add(name, SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), "root");

        add.Should().Throw<RejectedException>().WithMessage("*name*");
    }

    [Fact]
    public void should_refuse_a_name_that_is_too_long()
    {
        var add = () => Catalog().Add(new string('n', 101), SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), "root");

        add.Should().Throw<RejectedException>().WithMessage("*name*");
    }

    [Fact]
    public void should_refuse_a_name_that_is_already_taken_and_keep_the_first_source()
    {
        var catalog = Catalog();
        catalog.Add("cours", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("a"), "root");

        var again = () => catalog.Add("cours", SourceKind.Memory, SourceMode.ReadOnly, _workspace.Folder("b"), "root");

        again.Should().Throw<RejectedException>().WithMessage("*already exists*");
        _store.List().Should().ContainSingle().Which.Kind.Should().Be(SourceKind.Knowledge);
    }

    [Fact]
    public void should_remove_a_source_once_and_say_so_when_there_was_none()
    {
        var source = Catalog().Add("cours", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("a"), "root");

        _store.Remove(source.Id).Should().BeTrue();
        _store.Remove(source.Id).Should().BeFalse();
        _store.Get(source.Id).Should().BeNull();
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
        var source = catalog.Add("cours", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("a"), "alice");

        catalog.Remove(source.Id, SourceScope.All, "bob");

        _audit.Recent(10, "source.added").Should().ContainSingle().Which.Should().Match<AuditEvent>(e => e.Actor == "alice" && e.Target == "cours");
        _audit.Recent(10, "source.removed").Should().ContainSingle().Which.Actor.Should().Be("bob");
        _store.List().Should().BeEmpty();
        var again = () => catalog.Remove(source.Id, SourceScope.All, "bob");
        again.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public void should_show_a_caller_limited_to_some_sources_those_and_treat_the_others_as_absent()
    {
        var catalog = Catalog();
        var mine = catalog.Add("mine", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("a"), "root");
        var other = catalog.Add("other", SourceKind.Memory, SourceMode.ReadOnly, _workspace.Folder("b"), "root");
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
        catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), "root");
        catalog.Add("cours", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("k1"), "root");
        catalog.Add("travail", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("k2"), "root");
        catalog.Add("app-a", SourceKind.ApplicationDocs, SourceMode.ReadOnly, _workspace.Folder("a1"), "root");
        catalog.Add("app-b", SourceKind.ApplicationDocs, SourceMode.ReadOnly, _workspace.Folder("a2"), "root");

        var second = () => catalog.Add("memory-2", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m2"), "root");

        second.Should().Throw<RejectedException>().Which.Should().Match<RejectedException>(e => e.Problem == Problem.Conflict && e.Message.Contains("only 1") && !e.Message.Contains("memory"));
        _store.List().Should().HaveCount(5);
    }

    [Fact]
    public void should_say_for_each_kind_how_many_are_declared_and_whether_the_base_is_complete()
    {
        var catalog = Catalog();

        var before = catalog.Kinds();
        catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), "root");
        var after = catalog.Kinds();

        before.Single(k => k.Rule.Kind == SourceKind.Memory).Should().Match<KindStatus>(k => k.Count == 0 && !k.Satisfied && k.Rule.Required && k.Rule.Unique);
        before.Where(k => k.Rule.Kind != SourceKind.Memory).Should().OnlyContain(k => k.Satisfied && !k.Rule.Required);
        after.Single(k => k.Rule.Kind == SourceKind.Memory).Should().Match<KindStatus>(k => k.Count == 1 && k.Satisfied);
    }

    [Fact]
    public void should_not_let_the_required_memory_vault_be_forgotten_but_let_the_optional_ones_go()
    {
        var catalog = Catalog();
        var memory = catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), "root");
        var cours = catalog.Add("cours", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("k"), "root");

        var forget = () => catalog.Remove(memory.Id, SourceScope.All, "root");
        catalog.Remove(cours.Id, SourceScope.All, "root");

        forget.Should().Throw<RejectedException>().Which.Should().Match<RejectedException>(e => e.Problem == Problem.Conflict && e.Message.Contains("required"));
        _store.List().Should().ContainSingle().Which.Id.Should().Be(memory.Id);
    }

    [Fact]
    public void should_change_the_folder_the_mode_and_the_name_of_a_source_but_never_its_kind()
    {
        var catalog = Catalog();
        var memory = catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, _workspace.Folder("m"), "root");
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
        var one = catalog.Add("one", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("a"), "root");
        var two = catalog.Add("two", SourceKind.Knowledge, SourceMode.ReadWrite, _workspace.Folder("b"), "root");

        var outside = () => catalog.Update(one.Id, "one", SourceMode.ReadWrite, "/etc", SourceScope.All, "x");
        var taken = () => catalog.Update(one.Id, "two", SourceMode.ReadWrite, one.Path, SourceScope.All, "x");
        var hidden = () => catalog.Update(two.Id, "two", SourceMode.ReadWrite, two.Path, new SourceScope([one.Id]), "x");

        outside.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
        taken.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Conflict);
        hidden.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
        _store.List().Select(s => s.Name).Should().Equal("one", "two");
    }
}
