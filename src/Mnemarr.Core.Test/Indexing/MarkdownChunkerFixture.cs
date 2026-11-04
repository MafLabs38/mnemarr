using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Test.Indexing;

public sealed class MarkdownChunkerFixture
{
    [Fact]
    public void should_cut_a_note_at_its_headings_and_keep_the_path_of_headings()
    {
        var chunks = MarkdownChunker.Split("# Docker\nintro\n\n## Images\nlayers\n\n### Tags\nnames\n\n## Volumes\ndata");

        chunks.Should().Equal(
            new Chunk("Docker", "intro"),
            new Chunk("Docker > Images", "layers"),
            new Chunk("Docker > Images > Tags", "names"),
            new Chunk("Docker > Volumes", "data"));
    }

    [Fact]
    public void should_give_no_heading_to_the_text_before_the_first_one()
    {
        var chunks = MarkdownChunker.Split("preamble\n\n# Title\nbody");

        chunks.Select(chunk => chunk.Heading).Should().Equal(string.Empty, "Title");
    }

    [Fact]
    public void should_not_take_a_hash_line_in_a_code_block_for_a_heading()
    {
        var chunks = MarkdownChunker.Split("# Shell\n```bash\n# a comment\necho hi\n```\nafter");

        chunks.Should().ContainSingle().Which.Text.Should().Contain("# a comment").And.Contain("after");
    }

    [Fact]
    public void should_skip_a_section_that_has_no_text()
    {
        MarkdownChunker.Split("# One\n\n# Two\ntext").Should().Equal(new Chunk("Two", "text"));
        MarkdownChunker.Split(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void should_keep_a_level_that_was_skipped_out_of_the_path()
    {
        MarkdownChunker.Split("### Deep\ntext").Should().Equal(new Chunk("Deep", "text"));
    }

    [Fact]
    public void should_cut_a_long_section_between_paragraphs_so_that_no_chunk_is_over_the_limit()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 40)));

        var chunks = MarkdownChunker.Split(paragraphs, maxChars: 100);

        chunks.Should().HaveCountGreaterThan(1).And.OnlyContain(chunk => chunk.Text.Length <= 100);
        string.Concat(chunks.Select(chunk => chunk.Text)).Replace("\n", string.Empty, StringComparison.Ordinal)
            .Should().Be(string.Concat(Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 40))));
    }

    [Fact]
    public void should_cut_a_paragraph_that_is_too_long_at_a_space()
    {
        var chunks = MarkdownChunker.Split(string.Join(' ', Enumerable.Repeat("word", 60)), maxChars: 50);

        chunks.Should().OnlyContain(chunk => chunk.Text.Length <= 50 && !chunk.Text.StartsWith(' ') && !chunk.Text.EndsWith(' '));
        chunks.Sum(chunk => chunk.Text.Split(' ').Length).Should().Be(60);
    }

    [Fact]
    public void should_cut_a_word_that_is_longer_than_the_limit_instead_of_looping()
    {
        MarkdownChunker.Split(new string('x', 250), maxChars: 100).Should().HaveCount(3);
    }

    [Fact]
    public void should_reject_a_limit_below_one()
    {
        var split = () => MarkdownChunker.Split("text", maxChars: 0);

        split.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("## Learning C#", "Learning C#")]
    [InlineData("## Closed ##", "Closed")]
    [InlineData("## Closed with spaces  ##  ", "Closed with spaces")]
    [InlineData("##   Padded   ", "Padded")]
    [InlineData("##\tTabbed", "Tabbed")]
    public void should_read_the_title_of_a_heading_without_its_closing_marks_but_keeping_a_hash_that_belongs_to_it(string line, string expected)
    {
        MarkdownChunker.Split(line + "\ntext").Should().ContainSingle().Which.Heading.Should().Be(expected);
    }

    [Theory]
    [InlineData("#nospace")]
    [InlineData("####### seven")]
    [InlineData("#")]
    [InlineData("# ")]
    [InlineData("# ###")]
    [InlineData(" # indented")]
    public void should_not_take_a_line_that_is_not_a_heading_for_one(string line)
    {
        MarkdownChunker.Split(line + "\ntext").Should().ContainSingle().Which.Heading.Should().BeEmpty();
    }

    [Fact]
    public void should_cut_a_hostile_heading_in_linear_time()
    {
        // A heading followed by a very long run of spaces is what makes a backtracking pattern take cubic time.
        var hostile = "# a" + new string(' ', 200_000) + "x\n" + "#" + new string(' ', 200_000) + "\ntext";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var chunks = MarkdownChunker.Split(hostile);

        // A generous bound: a backtracking pattern takes minutes on this line, a single pass takes milliseconds.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        chunks.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("intro\n# The Title\n## Part", "The Title")]
    [InlineData("```\n# not a title\n```\nno heading", "note")]
    [InlineData("## only level two", "note")]
    public void should_title_a_note_with_its_first_level_one_heading_or_its_file_name(string markdown, string expected)
    {
        MarkdownChunker.Title(markdown, "folder/note.md").Should().Be(expected);
    }
}
