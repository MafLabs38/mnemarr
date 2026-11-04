using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Test.Notes;

public sealed class NoteIndexingFixture : NotesFixtureBase
{
    [Fact]
    public async Task should_replace_the_links_and_tags_of_a_note_that_changed_and_drop_those_of_a_note_that_went()
    {
        var source = await SourceWithAsync("s", ("a.md", "[[b]] #t"), ("b.md", "# B"), ("gone.md", "[[b]] #only-here"));

        TestWorkspace.Write(Path.Combine(Workspace.SourcesFolder, "s"), "a.md", "[[c]] #u");
        File.Delete(Path.Combine(Workspace.SourcesFolder, "s", "gone.md"));
        await ReindexAsync(source);

        var a = Browser.Find(source.Id, "a.md", SourceScope.All)!;
        a.Links.Select(l => l.Target).Should().Equal("c");
        Browser.Find(source.Id, "gone.md", SourceScope.All).Should().BeNull();
        a.Tags.Should().Equal("u");
        Browser.Find(source.Id, "b.md", SourceScope.All)!.Backlinks.Should().BeEmpty("a no longer links to b, and gone.md is not there any more");
        Browser.Tags(SourceScope.All).Tags.Select(t => t.Tag).Should().Equal("u");
        using var connection = Workspace.Database.Open();
        using var rows = connection.CreateCommand();
        rows.CommandText = "SELECT (SELECT COUNT(*) FROM note_links), (SELECT COUNT(*) FROM note_tags)";
        using var reader = rows.ExecuteReader();
        reader.Read().Should().BeTrue();
        (reader.GetInt64(0), reader.GetInt64(1)).Should().Be((1L, 1L), "the rows of a note that was replaced or removed go with it");
    }

    [Fact]
    public async Task should_read_the_links_and_tags_again_after_a_new_reading_without_cutting_the_note_again()
    {
        var source = await SourceWithAsync("s", ("a.md", "# A\n[[b]] #t\n\ntext"), ("b.md", "# B"));
        long chunkBefore;
        using (var connection = Workspace.Database.Open())
        {
            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE notes SET meta_version = 0; DELETE FROM note_links; DELETE FROM note_tags";
            stale.ExecuteNonQuery();
            using var chunk = connection.CreateCommand();
            chunk.CommandText = "SELECT MIN(c.id) FROM chunks c JOIN notes n ON n.id = c.note_id WHERE n.path = 'a.md'";
            chunkBefore = (long)chunk.ExecuteScalar()!;
        }

        var report = await ReindexAsync(source);

        report.Should().Be(new IndexReport(0, 0, 0, Unchanged: 2), "the notes were not cut again");
        Browser.Find(source.Id, "a.md", SourceScope.All)!.Should().Match<Mnemarr.Core.Notes.NoteDetail>(d => d.Links.Count == 1 && d.Tags.Count == 1);
        using var after = Workspace.Database.Open();
        using var check = after.CreateCommand();
        check.CommandText = "SELECT MIN(c.id) FROM chunks c JOIN notes n ON n.id = c.note_id WHERE n.path = 'a.md'";
        ((long)check.ExecuteScalar()!).Should().Be(chunkBefore, "the chunks are the same rows, so whatever is built on them (vectors) is kept");
    }

    [Fact]
    public async Task should_keep_the_images_of_a_source_up_to_date_and_forget_them_with_it()
    {
        var source = await SourceWithAsync("s", ("a.md", "# A"));
        var folder = Path.Combine(Workspace.SourcesFolder, "s");
        TestWorkspace.Write(folder, "img/one.png", "1");
        TestWorkspace.Write(folder, "img/two.svg", "<svg/>");
        TestWorkspace.Write(folder, ".hidden/three.png", "3");

        await ReindexAsync(source);
        File.Delete(Path.Combine(folder, "img", "one.png"));
        TestWorkspace.Write(folder, "four.JPG", "4");
        await ReindexAsync(source);

        Browser.HasImage(source.Id, "four.JPG").Should().BeTrue();
        Browser.HasImage(source.Id, "img/one.png").Should().BeFalse("it went");
        Browser.HasImage(source.Id, "img/two.svg").Should().BeFalse("not a format a note may show");
        Browser.HasImage(source.Id, ".hidden/three.png").Should().BeFalse("a hidden folder is not read");
    }
}
