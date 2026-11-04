using System.Runtime.Versioning;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Notes;

namespace Mnemarr.Core.Test.Notes;

public sealed class NoteReaderFixture : NotesFixtureBase
{
    [Fact]
    public async Task should_read_a_note_without_its_front_matter_with_its_tags_links_and_the_images_it_embeds()
    {
        var source = await SourceWithAsync(
            "courses", ("devops/docker.md", "---\ntags: [infra]\n---\n# Docker\nSee [[kubernetes]] ![[plan.png]] ![[missing.png]] ![[../outside.png]]"), ("devops/kubernetes.md", "# K8s"));
        TestWorkspace.Write(Path.Combine(Workspace.SourcesFolder, "courses"), "devops/plan.png", "not really a png");

        var content = Reader.Read(source.Id, "devops/docker.md", SourceScope.All);

        content.Markdown.Should().StartWith("# Docker").And.NotContain("tags:");
        content.Detail.Tags.Should().Equal("infra");
        content.Detail.Links.Single().Note!.Path.Should().Be("devops/kubernetes.md");
        content.Embeds.Should().BeEquivalentTo(new Dictionary<string, string> { ["plan.png"] = "devops/plan.png" }, "an image that is not there, or not inside the source, leads nowhere");
    }

    [Fact]
    public async Task should_find_an_embedded_image_by_its_name_anywhere_in_the_source_the_nearest_first_and_never_in_a_hidden_folder()
    {
        var source = await SourceWithAsync("courses", ("a.md", "![[plan.png]] ![[Assets/schema.PNG]] ![[hidden.png]] ![[nowhere.png]] ![[img/sub/two.png]]"));
        var folder = Path.Combine(Workspace.SourcesFolder, "courses");
        TestWorkspace.Write(folder, "attachments/plan.png", "far");
        TestWorkspace.Write(folder, "attachments/deep/er/plan.png", "farther");
        TestWorkspace.Write(folder, "all/Assets/schema.png", "by path");
        TestWorkspace.Write(folder, ".trash/hidden.png", "hidden");
        TestWorkspace.Write(folder, "x/img/sub/two.png", "suffix");

        var embeds = Reader.Read(source.Id, "a.md", SourceScope.All).Embeds;

        embeds.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["plan.png"] = "attachments/plan.png",
            ["Assets/schema.PNG"] = "all/Assets/schema.png",
            ["img/sub/two.png"] = "x/img/sub/two.png",
        });
    }

    [Theory]
    [InlineData("missing.md")]
    [InlineData("../other/secret.md")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../x.md")]
    [InlineData(".hidden/x.md")]
    [InlineData("")]
    public async Task should_refuse_a_path_that_is_not_an_indexed_note_of_the_source(string path)
    {
        var source = await SourceWithAsync("courses", ("a.md", "# A"));
        await SourceWithAsync("other", ("secret.md", "# Secret"));

        var read = () => Reader.Read(source.Id, path, SourceScope.All);

        read.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public async Task should_answer_not_found_for_a_source_the_caller_may_not_read_exactly_as_for_one_that_does_not_exist()
    {
        var mine = await SourceWithAsync("mine", ("a.md", "# A"));
        var other = await SourceWithAsync("other", ("b.md", "# B"));

        var hidden = () => Reader.Read(other.Id, "b.md", new SourceScope([mine.Id]));
        var absent = () => Reader.Read(Guid.NewGuid(), "b.md", new SourceScope([mine.Id]));

        hidden.Should().Throw<RejectedException>().Which.Should().Match<RejectedException>(e => e.Problem == Problem.NotFound && e.Message == "There is no such note.");
        absent.Should().Throw<RejectedException>().Which.Message.Should().Be("There is no such note.");
    }

    [Fact]
    public async Task should_serve_only_images_of_the_short_list_from_the_source_of_the_note()
    {
        var source = await SourceWithAsync("courses", ("a.md", "# A"));
        var folder = Path.Combine(Workspace.SourcesFolder, "courses");
        TestWorkspace.Write(folder, "img/plan.PNG", "png bytes");
        TestWorkspace.Write(folder, "img/logo.svg", "<svg onload=alert(1)/>");
        TestWorkspace.Write(folder, "img/page.html", "<script>alert(1)</script>");
        TestWorkspace.Write(folder, ".git/config.png", "hidden");

        using var image = Reader.OpenImage(source.Id, "img/plan.PNG", SourceScope.All).Content;
        var svg = () => Reader.OpenImage(source.Id, "img/logo.svg", SourceScope.All);
        var html = () => Reader.OpenImage(source.Id, "img/page.html", SourceScope.All);
        var hidden = () => Reader.OpenImage(source.Id, ".git/config.png", SourceScope.All);
        var up = () => Reader.OpenImage(source.Id, "../courses/img/plan.PNG", SourceScope.All);

        new StreamReader(image).ReadToEnd().Should().Be("png bytes");
        Reader.OpenImage(source.Id, "img/plan.PNG", SourceScope.All).ContentType.Should().Be("image/png");
        foreach (var refused in new[] { svg, html, hidden, up })
        {
            refused.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
        }
    }

    [Fact]
    public async Task should_not_serve_an_image_of_a_source_the_caller_may_not_read()
    {
        var mine = await SourceWithAsync("mine", ("a.md", "# A"));
        var other = await SourceWithAsync("other", ("b.md", "# B"));
        TestWorkspace.Write(Path.Combine(Workspace.SourcesFolder, "other"), "p.png", "x");

        var open = () => Reader.OpenImage(other.Id, "p.png", new SourceScope([mine.Id]));

        open.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public async Task should_refuse_an_image_that_is_too_large()
    {
        var source = await SourceWithAsync("courses", ("a.md", "# A"));
        using (var big = File.Create(Path.Combine(Workspace.SourcesFolder, "courses", "big.png")))
        {
            big.SetLength(NoteReader.MaxImageBytes + 1);
        }

        var open = () => Reader.OpenImage(source.Id, "big.png", SourceScope.All);

        open.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task should_not_read_through_a_link_that_replaced_a_note_or_a_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creating a link needs no privilege on Linux only.");
        var source = await SourceWithAsync("courses", ("a.md", "# A"), ("sub/b.md", "# B"));
        var outside = Directory.CreateTempSubdirectory("mnemarr-outside-");
        try
        {
            TestWorkspace.Write(outside.FullName, "secret.md", "# Secret");
            var folder = Path.Combine(Workspace.SourcesFolder, "courses");
            File.Delete(Path.Combine(folder, "a.md"));
            File.CreateSymbolicLink(Path.Combine(folder, "a.md"), Path.Combine(outside.FullName, "secret.md"));
            Directory.Delete(Path.Combine(folder, "sub"), recursive: true);
            Directory.CreateSymbolicLink(Path.Combine(folder, "sub"), outside.FullName);

            var file = () => Reader.Read(source.Id, "a.md", SourceScope.All);
            var inFolder = () => Reader.Read(source.Id, "sub/b.md", SourceScope.All);

            file.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
            inFolder.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task should_say_the_folder_is_not_available_when_the_source_left_the_roots()
    {
        var source = await SourceWithAsync("courses", ("a.md", "# A"));
        var narrowed = new NoteReader(new Mnemarr.Core.Sources.SqliteSourceStore(Workspace.Database), Browser, Workspace.Paths(Path.Combine(Workspace.Root, "elsewhere")));

        var read = () => narrowed.Read(source.Id, "a.md", SourceScope.All);

        read.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Conflict);
    }
}
