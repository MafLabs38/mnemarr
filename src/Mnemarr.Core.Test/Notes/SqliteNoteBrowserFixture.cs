using Mnemarr.Core.Accounts;
using Mnemarr.Core.Notes;

namespace Mnemarr.Core.Test.Notes;

public sealed class SqliteNoteBrowserFixture : NotesFixtureBase
{
    [Fact]
    public async Task should_list_the_notes_by_source_and_path_and_only_those_with_a_tag_when_asked()
    {
        var courses = await SourceWithAsync("courses", ("devops/docker.md", "# Docker\n#infra"), ("angular/signals.md", "# Signals"));
        await SourceWithAsync("memory", ("decisions/network.md", "# Network choice\n#infra #decision"));

        var all = Browser.List(SourceScope.All, null, null).Notes;
        var tagged = Browser.List(SourceScope.All, null, "INFRA").Notes;
        var one = Browser.List(SourceScope.All, courses.Id, null).Notes;
        var both = Browser.List(SourceScope.All, courses.Id, "infra").Notes;

        all.Select(n => $"{n.SourceName}:{n.Path}").Should().Equal("courses:angular/signals.md", "courses:devops/docker.md", "memory:decisions/network.md");
        all.Single(n => n.Path == "devops/docker.md").Should().Match<Mnemarr.Core.Notes.NoteEntry>(n => n.Title == "Docker" && n.Topic == "devops");
        tagged.Select(n => n.Path).Should().Equal("devops/docker.md", "decisions/network.md");
        one.Should().HaveCount(2);
        both.Select(n => n.Path).Should().Equal("devops/docker.md"); // a source and a tag together
    }

    [Fact]
    public async Task should_count_the_tags_of_the_notes_a_caller_may_read_most_used_first()
    {
        var mine = await SourceWithAsync("mine", ("a.md", "#infra #only-mine"), ("b.md", "#infra"));
        await SourceWithAsync("other", ("c.md", "#infra #secret"));

        var everyone = Browser.Tags(SourceScope.All).Tags;
        var scoped = Browser.Tags(new SourceScope([mine.Id])).Tags;

        everyone.Should().Equal(new Mnemarr.Core.Notes.TagCount("infra", 3), new Mnemarr.Core.Notes.TagCount("only-mine", 1), new Mnemarr.Core.Notes.TagCount("secret", 1));
        scoped.Should().Equal(new Mnemarr.Core.Notes.TagCount("infra", 2), new Mnemarr.Core.Notes.TagCount("only-mine", 1));
    }

    [Fact]
    public async Task should_resolve_a_link_by_name_across_sources_and_prefer_the_same_source_then_the_shortest_path()
    {
        var courses = await SourceWithAsync("courses", ("index.md", "[[docker]] [[nope]] [[devops/kubernetes]]"), ("devops/docker.md", "# Docker here"), ("devops/kubernetes.md", "# K8s"));
        await SourceWithAsync("memory", ("docker.md", "# Docker in memory"), ("deep/er/docker.md", "# Deeper"));

        var detail = Browser.Find(courses.Id, "index.md", SourceScope.All)!;

        detail.Links.Single(l => l.Target == "docker").Note.Should().Match<Mnemarr.Core.Notes.NoteRef>(n => n.SourceName == "courses" && n.Path == "devops/docker.md");
        detail.Links.Single(l => l.Target == "devops/kubernetes").Note!.Path.Should().Be("devops/kubernetes.md");
        detail.Links.Single(l => l.Target == "nope").Note.Should().BeNull();
    }

    [Fact]
    public async Task should_find_the_backlinks_of_a_note_by_its_name_or_its_path()
    {
        var courses = await SourceWithAsync("courses", ("devops/docker.md", "# Docker"), ("a.md", "[[docker]]"), ("b.md", "[[devops/docker|full path]]"), ("c.md", "[[docker]] [[docker]]"), ("d.md", "no link"), ("self.md", "[[self]]"));

        var backlinks = Browser.Find(courses.Id, "devops/docker.md", SourceScope.All)!.Backlinks;
        var self = Browser.Find(courses.Id, "self.md", SourceScope.All)!.Backlinks;

        backlinks.Select(b => b.Path).Should().Equal("a.md", "b.md", "c.md");
        self.Should().BeEmpty("a note that links to itself is not its own backlink");
    }

    [Fact]
    public async Task should_resolve_and_backlink_a_link_written_from_the_folder_of_its_note()
    {
        var source = await SourceWithAsync("courses", ("01 - Topic A/Index.md", "# Index"), ("04 - Topic B/Basics.md", "[[../01 - Topic A/Index|the index]] [[../../outside]]"));

        var basics = Browser.Find(source.Id, "04 - Topic B/Basics.md", SourceScope.All)!;
        var index = Browser.Find(source.Id, "01 - Topic A/Index.md", SourceScope.All)!;

        basics.Links.Should().ContainSingle().Which.Note!.Path.Should().Be("01 - Topic A/Index.md");
        index.Backlinks.Should().ContainSingle().Which.Path.Should().Be("04 - Topic B/Basics.md");
    }

    [Fact]
    public async Task should_list_a_note_as_a_backlink_only_of_the_note_its_link_leads_to_when_two_notes_have_the_same_name()
    {
        var a = await SourceWithAsync("a", ("docker.md", "# A docker"), ("linker.md", "[[docker]]"));
        var b = await SourceWithAsync("b", ("docker.md", "# B docker"));

        var inA = Browser.Find(a.Id, "docker.md", SourceScope.All)!;
        var inB = Browser.Find(b.Id, "docker.md", SourceScope.All)!;

        inA.Backlinks.Should().ContainSingle().Which.Path.Should().Be("linker.md", "the link leads to the note of its own source");
        inB.Backlinks.Should().BeEmpty("the same link does not lead here");
    }

    [Fact]
    public async Task should_not_resolve_link_or_backlink_through_a_source_the_caller_may_not_read()
    {
        var mine = await SourceWithAsync("mine", ("a.md", "[[secret-plan]]"), ("topic.md", "# Topic"));
        await SourceWithAsync("hidden", ("secret-plan.md", "# Secret [[topic]]"));
        var scope = new SourceScope([mine.Id]);

        var detail = Browser.Find(mine.Id, "a.md", scope)!;
        var topic = Browser.Find(mine.Id, "topic.md", scope)!;
        var allowed = Browser.Find(mine.Id, "topic.md", SourceScope.All)!;

        detail.Links.Single().Note.Should().BeNull("the note exists, but not for this caller");
        topic.Backlinks.Should().BeEmpty();
        allowed.Backlinks.Should().ContainSingle().Which.SourceName.Should().Be("hidden");
    }

    [Fact]
    public async Task should_answer_nothing_for_a_note_that_is_not_indexed_or_out_of_scope()
    {
        var mine = await SourceWithAsync("mine", ("a.md", "# A"));
        var other = await SourceWithAsync("other", ("b.md", "# B"));

        Browser.Find(mine.Id, "missing.md", SourceScope.All).Should().BeNull();
        Browser.Find(other.Id, "b.md", new SourceScope([mine.Id])).Should().BeNull();
        Browser.Find(other.Id, "b.md", new SourceScope([])).Should().BeNull();
        Browser.List(new SourceScope([]), null, null).Notes.Should().BeEmpty("an empty scope matches nothing, never everything");
    }

    [Fact]
    public async Task should_treat_percent_and_underscore_in_a_link_as_text_not_as_a_pattern()
    {
        var source = await SourceWithAsync("s", ("x/100%_done.md", "# Done"), ("x/1000 done.md", "# Not it"), ("a.md", "[[x/100%_done]] [[x/100_%]]"));

        var detail = Browser.Find(source.Id, "a.md", SourceScope.All)!;

        detail.Links.Single(l => l.Target == "x/100%_done").Note!.Path.Should().Be("x/100%_done.md");
        detail.Links.Single(l => l.Target == "x/100_%").Note.Should().BeNull();
    }

    [Fact]
    public async Task should_say_how_many_notes_there_are_when_the_tree_is_cut_short()
    {
        var source = await SourceWithAsync("big", ("a.md", "# A"));
        using (var connection = Workspace.Database.Open())
        {
            using var fill = connection.CreateCommand();
            fill.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 10100)
                INSERT INTO notes (source_id, path, topic, title, content_hash, indexed_at) SELECT $source, 'bulk/' || i || '.md', 'bulk', 'Bulk ' || i, 'h', 'now' FROM n
                """;
            fill.Parameters.AddWithValue("$source", source.Id.ToString());
            fill.ExecuteNonQuery();
        }

        var listing = Browser.List(SourceScope.All, null, null);

        listing.Notes.Should().HaveCount(SqliteNoteBrowser.MaxEntries);
        listing.Total.Should().Be(10_101, "the list is cut, and the total says by how much");
    }

    [Fact]
    public async Task should_order_tags_by_use_then_by_name_and_count_them_all_even_when_the_list_is_cut()
    {
        await SourceWithAsync("s", ("a.md", "#beta #alpha #gamma"), ("b.md", "#gamma"));

        var tags = Browser.Tags(SourceScope.All);

        tags.Tags.Select(t => t.Tag).Should().Equal("gamma", "alpha", "beta");
        tags.Total.Should().Be(3);
    }

    [Fact]
    public async Task should_resolve_a_link_that_is_the_end_of_a_path_and_list_it_as_a_backlink_of_that_note()
    {
        var source = await SourceWithAsync("s", ("a.md", "[[devops/docker]] [[b/c]]"), ("tools/devops/docker.md", "# Docker"), ("x/b/c.md", "# C"), ("y/b/c.md", "# Other C, longer path"));

        var detail = Browser.Find(source.Id, "a.md", SourceScope.All)!;
        var docker = Browser.Find(source.Id, "tools/devops/docker.md", SourceScope.All)!;

        detail.Links.Select(l => (l.Target, l.Note?.Path)).Should().BeEquivalentTo([("devops/docker", "tools/devops/docker.md"), ("b/c", "x/b/c.md")]);
        docker.Backlinks.Should().ContainSingle().Which.Path.Should().Be("a.md");
    }

    [Fact]
    public async Task should_resolve_a_link_to_a_note_whose_folder_and_name_have_accents_and_capitals()
    {
        var source = await SourceWithAsync("s", ("Cours/Été.md", "# Été"), ("Cours/Élèves.md", "[[Cours/Été]] [[élèves]] [[COURS/ÉTÉ|again]]"));

        var detail = Browser.Find(source.Id, "Cours/Élèves.md", SourceScope.All)!;
        var summer = Browser.Find(source.Id, "Cours/Été.md", SourceScope.All)!;

        detail.Links.Should().HaveCount(2, "the first and the third say the same thing").And.OnlyContain(l => l.Note != null, "SQLite only folds ASCII: the case is folded in code, as when the index was written");
        summer.Backlinks.Should().ContainSingle().Which.Path.Should().Be("Cours/Élèves.md");
    }

    [Fact]
    public async Task should_list_a_note_that_embeds_another_as_a_backlink_of_it()
    {
        var source = await SourceWithAsync("s", ("Other note.md", "# Other"), ("host.md", "Shown in place: ![[Other note]]"));

        var other = Browser.Find(source.Id, "Other note.md", SourceScope.All)!;
        var host = Browser.Find(source.Id, "host.md", SourceScope.All)!;

        other.Backlinks.Should().ContainSingle().Which.Path.Should().Be("host.md");
        host.Links.Should().ContainSingle().Which.Note!.Path.Should().Be("Other note.md");
    }

    [Fact]
    public async Task should_find_a_link_written_with_a_dot_from_a_folder_whose_name_has_capitals()
    {
        var source = await SourceWithAsync("s", ("Folder/a.md", "[[./X]] [[../Top]]"), ("Folder/x.md", "# X"), ("Top.md", "# Top"));

        var detail = Browser.Find(source.Id, "Folder/a.md", SourceScope.All)!;
        var x = Browser.Find(source.Id, "Folder/x.md", SourceScope.All)!;

        detail.Links.Select(l => (l.Target, l.Note?.Path)).Should().BeEquivalentTo([("./x", "Folder/x.md"), ("../top", "Top.md")], "the link is returned as written, and resolved from the folder of its note");
        x.Backlinks.Should().ContainSingle().Which.Path.Should().Be("Folder/a.md");
    }
}
