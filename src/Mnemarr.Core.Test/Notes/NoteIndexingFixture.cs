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
        Browser.Find(source.Id, "a.md", SourceScope.All)!.Links.Select(l => l.Target).Should().Equal("c");
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
}
