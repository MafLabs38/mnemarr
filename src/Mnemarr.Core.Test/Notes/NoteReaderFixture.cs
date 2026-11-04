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
        await ReindexAsync(source);

        var content = Reader.Read(source.Id, "devops/docker.md", SourceScope.All);

        content.Markdown.Should().StartWith("# Docker").And.NotContain("tags:");
        content.Detail.Tags.Should().Equal("infra");
        content.Detail.Links.Single().Note!.Path.Should().Be("devops/kubernetes.md");
        content.Rendered.Should().Contain("![plan.png](mnemarr-file:devops%2Fplan.png)");
        content.Rendered.Should().Contain("![missing.png](mnemarr-unresolved:)").And.Contain("![../outside.png](mnemarr-unresolved:)", "an image that is not there, or not inside the source, leads nowhere");
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
        await ReindexAsync(source);

        var rendered = Reader.Read(source.Id, "a.md", SourceScope.All).Rendered;

        rendered.Should().Contain("![plan.png](mnemarr-file:attachments%2Fplan.png)");
        rendered.Should().Contain("![Assets/schema.PNG](mnemarr-file:all%2FAssets%2Fschema.png)");
        rendered.Should().Contain("![img/sub/two.png](mnemarr-file:x%2Fimg%2Fsub%2Ftwo.png)");
        rendered.Should().Contain("![hidden.png](mnemarr-unresolved:)").And.Contain("![nowhere.png](mnemarr-unresolved:)");
    }

    [Fact]
    public async Task should_rewrite_the_links_of_a_note_to_where_they_lead_and_leave_code_and_what_names_nothing_as_it_is()
    {
        var source = await SourceWithAsync(
            "courses",
            ("devops/docker.md", "# Docker\nSee [[kubernetes]], [[../index|the [index]], [[ghost|a ghost]], [[../../outside]], [[#heading]] and ![[Other note]].\n`[[kubernetes]]` and\n```\n[[kubernetes]]\n```\n[[Kubernetes#Pods|pods]]"),
            ("devops/kubernetes.md", "# K8s"),
            ("index.md", "# Index"),
            ("Other note.md", "# Other"));

        var rendered = Reader.Read(source.Id, "devops/docker.md", SourceScope.All).Rendered;

        rendered.Should().Contain($"[kubernetes](mnemarr-note:{source.Id}/devops%2Fkubernetes.md)");
        rendered.Should().Contain($"[the \\[index](mnemarr-note:{source.Id}/index.md)", "a link written from the folder of its note, with a bracket in its text that cannot end it early");
        rendered.Should().Contain("[a ghost](mnemarr-unresolved:)").And.Contain("[../../outside](mnemarr-unresolved:)");
        rendered.Should().Contain("[[#heading]]", "a link that names nothing is left as it was written");
        rendered.Should().Contain($"[Other note](mnemarr-note:{source.Id}/Other%20note.md)", "a note shown in place is a link");
        rendered.Should().Contain("`[[kubernetes]]`").And.Contain("```\n[[kubernetes]]\n```", "code is left as it is");
        rendered.Should().Contain($"[pods](mnemarr-note:{source.Id}/devops%2Fkubernetes.md)");
    }

    [Fact]
    public async Task should_rewrite_a_link_to_a_note_the_caller_may_not_read_as_a_link_that_leads_nowhere()
    {
        var mine = await SourceWithAsync("mine", ("a.md", "[[secret]]"));
        await SourceWithAsync("hidden", ("secret.md", "# Secret"));

        var rendered = Reader.Read(mine.Id, "a.md", new SourceScope([mine.Id])).Rendered;

        rendered.Should().Be("[secret](mnemarr-unresolved:)", "the note exists, but not for this caller");
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
        await ReindexAsync(source);

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
    public async Task should_refuse_a_note_that_grew_past_the_limit_since_the_pass_and_ask_to_try_again_for_one_that_went()
    {
        var source = await SourceWithAsync("courses", ("big.md", "# Big"), ("gone.md", "# Gone"));
        var folder = Path.Combine(Workspace.SourcesFolder, "courses");
        File.WriteAllText(Path.Combine(folder, "big.md"), new string('x', (int)Mnemarr.Core.Indexing.MarkdownFileScanner.MaxFileBytes + 10));
        File.Delete(Path.Combine(folder, "gone.md"));

        var big = () => Reader.Read(source.Id, "big.md", SourceScope.All);
        var gone = () => Reader.Read(source.Id, "gone.md", SourceScope.All);

        big.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound, "it is not a note any more");
        gone.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Conflict, "the file is not there now; the next pass will say");
    }

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.JPG", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.avif", "image/avif")]
    [InlineData("a.bmp", "image/bmp")]
    [InlineData("a.svg", null)]
    [InlineData("a.html", null)]
    [InlineData("a", null)]
    public void should_decide_the_type_of_an_image_by_its_extension_and_refuse_the_rest(string file, string? expected)
    {
        Mnemarr.Core.Indexing.ImageFormats.ContentType(file).Should().Be(expected);
    }

    [Fact]
    public async Task should_not_serve_an_image_the_last_pass_did_not_find_and_serve_it_once_it_did()
    {
        var source = await SourceWithAsync("courses", ("a.md", "# A"));
        TestWorkspace.Write(Path.Combine(Workspace.SourcesFolder, "courses"), "new.png", "png");

        var before = () => Reader.OpenImage(source.Id, "new.png", SourceScope.All);
        before.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound, "only what the index knows is served");
        await ReindexAsync(source);
        using var after = Reader.OpenImage(source.Id, "new.png", SourceScope.All).Content;

        new StreamReader(after).ReadToEnd().Should().Be("png");
    }

    [Fact]
    public async Task should_not_serve_an_image_of_a_source_the_caller_may_not_read()
    {
        var mine = await SourceWithAsync("mine", ("a.md", "# A"));
        var other = await SourceWithAsync("other", ("b.md", "# B"));
        TestWorkspace.Write(Path.Combine(Workspace.SourcesFolder, "other"), "p.png", "x");
        await ReindexAsync(other);

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

        await ReindexAsync(source);

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
