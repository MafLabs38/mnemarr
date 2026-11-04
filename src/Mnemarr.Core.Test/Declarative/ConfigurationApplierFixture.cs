using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Declarative;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Declarative;

public sealed class ConfigurationApplierFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteSourceStore _store;
    private readonly SqliteAuditLog _audit;
    private readonly SourceCatalog _catalog;
    private readonly ConfigurationApplier _subject;

    public ConfigurationApplierFixture()
    {
        _store = new SqliteSourceStore(_workspace.Database);
        _audit = new SqliteAuditLog(_workspace.Database, _clock);
        _catalog = new SourceCatalog(_store, _audit, _workspace.Paths(), _clock);
        _subject = new ConfigurationApplier(_catalog, _store, _audit);
    }

    public void Dispose() => _workspace.Dispose();

    private string Folder(string name) => _workspace.Folder(name);

    private ConfigurationDocument Document(string? memory = null, string[]? knowledge = null, string[]? docs = null) => new()
    {
        Memory = memory is null ? null : new SourceEntry { Path = Folder(memory) },
        Knowledge = knowledge is null ? null : [.. knowledge.Select(name => new SourceEntry { Name = name, Path = Folder(name) })],
        ApplicationDocs = docs is null ? null : [.. docs.Select(name => new SourceEntry { Name = name, Path = Folder(name), Mode = "ReadOnly" })],
    };

    [Fact]
    public void should_declare_what_the_document_says_as_managed_sources_with_the_default_name_and_mode_of_the_kind()
    {
        var plan = _subject.Apply(Document(memory: "m", knowledge: ["courses", "work"], docs: ["portarr"]), "ci");

        plan.Valid.Should().BeTrue();
        plan.Items.Should().OnlyContain(i => i.Action == PlanAction.Create);
        var sources = _store.List();
        sources.Should().OnlyContain(s => s.Managed);
        sources.Select(s => s.Name).Should().BeEquivalentTo("memory", "courses", "work", "portarr");
        sources.Single(s => s.Name == "memory").Mode.Should().Be(SourceMode.ReadOnly, "writes are off unless a source asks for them");
        sources.Single(s => s.Name == "portarr").Mode.Should().Be(SourceMode.ReadOnly);
        _audit.Recent(10, "config.applied").Should().ContainSingle().Which.Actor.Should().Be("ci");
        _audit.Recent(10, "source.added").Should().HaveCount(4, "a source that configuration makes is recorded like one made by hand, with its folder and mode");
    }

    [Fact]
    public void should_change_nothing_when_the_same_document_is_applied_again()
    {
        var document = Document(memory: "m", knowledge: ["courses"]);
        _subject.Apply(document, "ci");
        var before = _store.List();

        var plan = _subject.Apply(document, "ci");

        plan.Items.Should().OnlyContain(i => i.Action == PlanAction.Keep);
        _store.List().Should().BeEquivalentTo(before);
        _audit.Recent(10, "config.applied").Should().ContainSingle("a pass that changes nothing is not an event");
    }

    [Fact]
    public void should_update_a_managed_source_whose_folder_or_mode_changed_and_remove_the_managed_one_the_document_no_longer_lists()
    {
        _subject.Apply(Document(knowledge: ["courses", "work"]), "ci");
        var changed = Document(knowledge: ["courses"]);
        changed.Knowledge![0].Mode = "ReadWrite";

        var plan = _subject.Apply(changed, "ci");

        plan.Items.Select(i => (i.Name, i.Action)).Should().BeEquivalentTo([("courses", PlanAction.Update), ("work", PlanAction.Remove)]);
        _store.List().Should().ContainSingle().Which.Should().Match<Source>(s => s.Name == "courses" && s.Mode == SourceMode.ReadWrite);
    }

    [Fact]
    public void should_adopt_a_source_made_by_hand_and_never_remove_one_it_does_not_own()
    {
        var byHand = _catalog.Add("courses", SourceKind.Knowledge, SourceMode.ReadWrite, Folder("courses-old"), SourceScope.All, "alice");
        var untouched = _catalog.Add("notes-personal", SourceKind.Knowledge, SourceMode.ReadWrite, Folder("perso"), SourceScope.All, "alice");

        var plan = _subject.Apply(Document(knowledge: ["courses"]), "ci");

        plan.Items.Should().ContainSingle().Which.Action.Should().Be(PlanAction.Adopt);
        _store.Get(byHand.Id)!.Should().Match<Source>(s => s.Managed && s.Path == Folder("courses"));
        _store.Get(untouched.Id).Should().NotBeNull("what is made by hand is not the document's to remove");
        _store.Get(untouched.Id)!.Managed.Should().BeFalse();
    }

    [Fact]
    public void should_leave_alone_a_section_that_is_absent_and_empty_one_that_is_present()
    {
        var m = _catalog.Add("memory", SourceKind.Memory, SourceMode.ReadWrite, Folder("m"), SourceScope.All, "alice");
        _subject.Apply(Document(knowledge: ["courses"], docs: ["portarr"]), "ci");

        _subject.Apply(new ConfigurationDocument { ApplicationDocs = [] }, "ci");

        _store.List().Select(s => s.Name).Should().BeEquivalentTo("memory", "courses");
        _store.Get(m.Id)!.Managed.Should().BeFalse("a section that is absent is not managed");
    }

    [Fact]
    public void should_take_over_the_one_memory_vault_whatever_its_name_and_never_remove_it()
    {
        var m = _catalog.Add("old-memory", SourceKind.Memory, SourceMode.ReadWrite, Folder("old"), SourceScope.All, "alice");

        var plan = _subject.Apply(Document(memory: "new"), "ci");
        _subject.Apply(new ConfigurationDocument { Knowledge = [] }, "ci");

        plan.Items.Should().ContainSingle().Which.Action.Should().Be(PlanAction.Adopt);
        _store.Get(m.Id)!.Should().Match<Source>(s => s.Name == "memory" && s.Managed && s.Path == Folder("new"));
        _store.List().Should().ContainSingle("the memory vault is required: a document without it does not remove it");
    }

    [Fact]
    public void should_apply_none_of_a_document_that_has_an_error_and_say_every_error()
    {
        var document = Document(knowledge: ["courses"]);
        document.Knowledge!.Add(new SourceEntry { Name = "elsewhere", Path = "/etc" });
        document.Knowledge.Add(new SourceEntry { Name = "courses", Path = Folder("again") });
        document.Knowledge.Add(new SourceEntry { Path = Folder("nameless"), Mode = "Append" });

        var plan = _subject.Apply(document, "ci");

        plan.Valid.Should().BeFalse();
        plan.Errors.Should().HaveCount(3);
        plan.Errors.Should().Contain(e => e.Contains("inside one of the folders")).And.Contain(e => e.Contains("more than one entry")).And.Contain(e => e.Contains("no name")).And.Contain(e => e.Contains("ReadOnly or ReadWrite"));
        _store.List().Should().BeEmpty("one error and nothing is changed");
    }

    [Fact]
    public void should_refuse_a_name_that_belongs_to_a_source_of_another_kind()
    {
        _catalog.Add("shared", SourceKind.ApplicationDocs, SourceMode.ReadOnly, Folder("a"), SourceScope.All, "alice");

        var plan = _subject.Apply(Document(knowledge: ["shared"]), "ci");

        plan.Errors.Should().ContainSingle().Which.Should().Contain("already taken by a source of the kind ApplicationDocs");
    }

    [Fact]
    public void should_say_what_it_would_do_without_doing_it()
    {
        var plan = _subject.Plan(Document(memory: "m", knowledge: ["courses"]));

        plan.Items.Should().HaveCount(2).And.OnlyContain(i => i.Action == PlanAction.Create);
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public void should_export_only_the_sources_a_caller_may_touch()
    {
        var one = _catalog.Add("one", SourceKind.Knowledge, SourceMode.ReadWrite, Folder("a"), SourceScope.All, "alice");
        _catalog.Add("two", SourceKind.Knowledge, SourceMode.ReadWrite, Folder("b"), SourceScope.All, "alice");

        var text = _subject.Export(new SourceScope([one.Id]));

        text.Should().Contain("one").And.NotContain("two");
    }

    [Fact]
    public void should_refuse_to_change_or_forget_by_hand_a_source_the_document_owns()
    {
        _subject.Apply(Document(knowledge: ["courses"]), "ci");
        var owned = _store.List().Single();

        var change = () => _catalog.Update(owned.Id, "courses", SourceMode.ReadOnly, owned.Path, SourceScope.All, "alice");
        var forget = () => _catalog.Remove(owned.Id, SourceScope.All, "alice");

        change.Should().Throw<RejectedException>().Which.Should().Match<RejectedException>(e => e.Problem == Problem.Conflict && e.Message.Contains("managed by configuration"));
        forget.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Conflict);
        _store.Get(owned.Id).Should().Be(owned);
    }

    [Fact]
    public void should_report_an_entry_that_is_empty_instead_of_failing()
    {
        var document = Document(knowledge: ["courses"]);
        document.Knowledge!.Add(null!);

        var apply = () => _subject.Apply(document, "ci");

        apply.Should().NotThrow().Which.Errors.Should().ContainSingle().Which.Should().Be("knowledge[1]: it is empty.");
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public void should_let_the_document_take_again_a_name_that_it_frees_by_removing_a_source()
    {
        _subject.Apply(Document(knowledge: ["shared"]), "ci");

        var plan = _subject.Apply(Document(knowledge: [], docs: ["shared"]), "ci");

        plan.Valid.Should().BeTrue();
        plan.Items.Select(i => (i.Kind, i.Action)).Should().BeEquivalentTo([(SourceKind.Knowledge, PlanAction.Remove), (SourceKind.ApplicationDocs, PlanAction.Create)]);
        _store.List().Should().ContainSingle().Which.Kind.Should().Be(SourceKind.ApplicationDocs);
    }

    [Fact]
    public void should_refuse_to_move_a_managed_source_outside_the_roots_and_keep_its_folder()
    {
        _subject.Apply(Document(knowledge: ["courses"]), "ci");
        var moved = new ConfigurationDocument { Knowledge = [new SourceEntry { Name = "courses", Path = "/etc" }] };

        var plan = _subject.Apply(moved, "ci");

        plan.Errors.Should().ContainSingle().Which.Should().Contain("inside one of the folders");
        _store.List().Should().ContainSingle().Which.Path.Should().Be(Folder("courses"));
    }

    [Fact]
    public void should_name_what_was_removed_in_the_audit_trail()
    {
        _subject.Apply(Document(knowledge: ["courses", "work"]), "ci");

        _subject.Apply(Document(knowledge: ["courses"]), "ci");

        _audit.Recent(10, "config.applied").First().Detail.Should().Contain("1 remove").And.Contain("removed: work");
    }
}
