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
    public void should_keep_embedded_images_apart_from_links_and_take_an_embedded_note_for_a_link()
    {
        var meta = MarkdownNotes.Parse("![[plan.png]] and ![[images/schema.png|300]] but [[a note]] and ![[Other note#Part]]");

        meta.Embeds.Should().Equal("plan.png", "images/schema.png");
        meta.Links.Select(link => link.Target).Should().Equal("a note", "other note");
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

    [Theory]
    [InlineData("Docker", "docker")]
    [InlineData("  Devops\\Docker.MD#Images ", "devops/docker")]
    [InlineData("/devops/docker/", "devops/docker")]
    [InlineData("notes.markdown", "notes")]
    [InlineData("./Sibling", "./sibling")]
    [InlineData("../Up/Note.md#Heading", "../up/note")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("#only-a-heading", null)]
    public void should_normalise_the_target_of_a_link(string written, string? expected)
    {
        MarkdownNotes.NormalizeTarget(written).Should().Be(expected);
    }

    [Fact]
    public void should_rewrite_links_and_embeds_outside_code_with_the_same_reading_as_the_index()
    {
        var body = "a [[one]] b ![[img.png|300]]\n`[[code]]` [[two|text]]\n```\n[[fenced]]\n```\n[[never closed";

        var rewritten = MarkdownNotes.Rewrite(body, token => $"<{(token.Embedded ? "embed" : "link")}:{token.Inner}>");

        rewritten.Should().Be("a <link:one> b <embed:img.png|300>\n`[[code]]` <link:two|text>\n```\n[[fenced]]\n```\n[[never closed");
    }

    [Fact]
    public void should_leave_a_link_as_written_when_the_replacement_says_nothing_and_keep_line_breaks_and_a_missing_last_one()
    {
        MarkdownNotes.Rewrite("[[a]]\r\n[[b]]", token => token.Inner == "a" ? null : "B").Should().Be("[[a]]\n" + "B");
        MarkdownNotes.Rewrite("", _ => "x").Should().BeEmpty();
        MarkdownNotes.Rewrite("no links\n", _ => "x").Should().Be("no links\n");
    }

    [Fact]
    public async Task should_rewrite_a_hostile_line_of_brackets_in_one_pass()
    {
        var hostile = new string('[', 1_000_000) + "\n[[a]]";

        var rewriting = Task.Run(() => MarkdownNotes.Rewrite(hostile, _ => "x"), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(rewriting, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        finished.Should().BeSameAs(rewriting, "a note must be rewritten in one pass, whatever it holds");
        (await rewriting).Should().EndWith("\nx");
    }

    [Theory]
    [InlineData("04 - Topic B/Basics.md", "../01 - Topic A/Index", "01 - topic a/index")]
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

    /// <summary>Parses on another thread and fails, instead of hanging, when it does not finish: a reading that is not linear must not wait for the end of the run.</summary>
    private static async Task<NoteMeta> ParsedWithinAsync(string text)
    {
        var parsing = Task.Run(() => MarkdownNotes.Parse(text), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(parsing, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(parsing, "a note must be read in one pass, whatever it holds");
        return await parsing;
    }

    [Fact]
    public async Task should_bound_what_a_hostile_note_can_make_it_keep()
    {
        var hostile = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"[[n{i}]] #t{i} ")) + new string('[', 100_000) + "]]" + new string(' ', 100_000);

        var meta = await ParsedWithinAsync(hostile + string.Concat(Enumerable.Range(0, 5_000).Select(i => $"![[f{i}.png]]")));

        meta.Links.Count.Should().BeLessThanOrEqualTo(200);
        meta.Tags.Count.Should().BeLessThanOrEqualTo(200);
        meta.Embeds.Count.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task should_read_a_line_of_brackets_that_never_close_in_one_pass_and_still_read_its_tags()
    {
        var line = new string('[', 2_000_000) + " #tag";

        var meta = await ParsedWithinAsync(line);

        meta.Links.Should().BeEmpty();
        meta.Tags.Should().Equal("tag");
    }

    [Fact]
    public void should_not_take_the_anchor_of_a_link_for_a_tag_and_still_read_a_tag_after_a_link_that_is_never_closed()
    {
        var meta = MarkdownNotes.Parse("[setup](#setup) and (#inline) then [[never closed #after");

        meta.Tags.Should().Equal("after");
        meta.Links.Should().BeEmpty();
    }
}
