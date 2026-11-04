using System.Text.Json;
using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Test.Indexing;

public sealed class MarkdownNotesFixture
{
    [Fact]
    public void should_read_wikilinks_with_their_text_heading_and_extension_the_way_obsidian_does()
    {
        var meta = MarkdownNotes.Parse("See [[Docker]], [[devops/Kubernetes|the cluster]], [[Notes.md#Setup]] and [[Docker]] again.");

        meta.Links.Should().BeEquivalentTo([new WikiLink("docker", null), new WikiLink("devops/kubernetes", "the cluster"), new WikiLink("notes", null)]);
    }

    [Fact]
    public void should_keep_embedded_files_apart_from_links()
    {
        var meta = MarkdownNotes.Parse("![[plan.png]] and ![[images/schema.png|300]] but [[a note]]");

        meta.Embeds.Should().Equal("plan.png", "images/schema.png");
        meta.Links.Should().ContainSingle().Which.Target.Should().Be("a note");
    }

    [Fact]
    public void should_read_tags_but_not_a_heading_a_number_a_language_or_code()
    {
        var meta = MarkdownNotes.Parse("# Title\n\nWork #Docker and #devops/ci, issue #123, C# is fine, (#inline) ok.\n`#incode` and [[x#heading]]\n```\n#fenced\n[[inside]]\n```\n");

        meta.Tags.Should().BeEquivalentTo("docker", "devops/ci");
        meta.Links.Select(link => link.Target).Should().Equal("x");
    }

    [Theory]
    [InlineData("---\ntags: [Alpha, beta]\n---\nBody", new[] { "alpha", "beta" })]
    [InlineData("---\ntags: alpha, beta\n---\nBody", new[] { "alpha", "beta" })]
    [InlineData("---\ntitle: x\ntags:\n  - alpha\n  - \"#beta\"\nauthor: me\n---\nBody #gamma", new[] { "alpha", "beta", "gamma" })]
    [InlineData("---\ntags: [never]\nBody without a closing fence", new string[0])]
    public void should_read_the_tags_of_the_front_matter_in_its_three_forms_and_only_when_it_is_closed(string note, string[] expected)
    {
        MarkdownNotes.Parse(note).Tags.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void should_give_the_body_without_front_matter_or_byte_order_mark()
    {
        MarkdownNotes.Body("﻿---\ntags: [a]\n---\n# Title\ntext").Should().Be("# Title\ntext");
        MarkdownNotes.Body("# No front matter\n---\nnot front matter").Should().Be("# No front matter\n---\nnot front matter");
    }

    /// <summary>The cases of <c>tests/shared/link-targets.json</c>, which the interface's own reading of a link is tested against too: the two must agree.</summary>
    public static TheoryData<string, string?> SharedTargets()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "tests", "shared", "link-targets.json")))
        {
            folder = folder.Parent;
        }

        var cases = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(File.ReadAllText(Path.Combine(folder!.FullName, "tests", "shared", "link-targets.json")))!;
        var data = new TheoryData<string, string?>();
        foreach (var c in cases)
        {
            data.Add(c["written"]!, c["target"]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SharedTargets))]
    public void should_normalise_the_target_of_a_link_as_the_interface_does(string written, string? expected)
    {
        MarkdownNotes.NormalizeTarget(written).Should().Be(expected);
    }

    [Theory]
    [InlineData("04 - Angular/Basics.md", "../01 - Java/Index", "01 - java/index")]
    [InlineData("a/b/c.md", "../../x", "x")]
    [InlineData("a/b/c.md", "./d", "a/b/d")]
    [InlineData("c.md", "../outside", null)]
    [InlineData("a/c.md", "../../outside", null)]
    [InlineData("a/c.md", "docker", "docker")]
    [InlineData("a/c.md", "devops/docker", "devops/docker")]
    public void should_read_a_link_written_from_the_folder_of_its_note_as_a_path_from_the_root_of_the_source(string note, string written, string? expected)
    {
        var meta = new NoteMeta([new WikiLink(written.ToLowerInvariant(), null)], [], []).ResolvedFrom(note);

        meta.Links.Select(link => link.Target).Should().Equal(expected is null ? [] : [expected.ToLowerInvariant()]);
    }

    [Fact]
    public void should_bound_what_a_hostile_note_can_make_it_keep()
    {
        var hostile = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"[[n{i}]] #t{i} ")) + new string('[', 100_000) + "]]" + new string(' ', 100_000);

        var meta = MarkdownNotes.Parse(hostile);

        meta.Links.Count.Should().BeLessThanOrEqualTo(200);
        meta.Tags.Count.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public void should_not_take_the_anchor_of_a_link_for_a_tag_and_still_read_a_tag_after_a_link_that_is_never_closed()
    {
        var meta = MarkdownNotes.Parse("[setup](#setup) and (#inline) then [[never closed #after");

        meta.Tags.Should().Equal("after");
        meta.Links.Should().BeEmpty();
    }
}
