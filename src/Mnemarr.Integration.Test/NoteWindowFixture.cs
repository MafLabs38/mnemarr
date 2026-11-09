using Mnemarr.Api.V1.Mcp;

namespace Mnemarr.Integration.Test;

/// <summary>How a long note is handed to a model in parts, without cutting anything silently.</summary>
public sealed class NoteWindowFixture
{
    private static readonly string Long = string.Concat(Enumerable.Range(0, 100_000).Select(index => (char)('a' + (index % 26))));

    [Fact]
    public void should_give_a_short_note_whole()
    {
        var window = NoteWindow.Of("short", null, null, tail: false, around: null);

        window.Should().Be(new NoteWindow("short", 5, 0, false, null));
    }

    [Fact]
    public void should_give_the_start_of_a_long_note_and_say_where_to_read_on()
    {
        var window = NoteWindow.Of(Long, null, null, tail: false, around: null);

        window.Text.Should().HaveLength(NoteWindow.MaxLength);
        (window.Total, window.Offset, window.Truncated, window.NextOffset).Should().Be((100_000, 0, true, NoteWindow.MaxLength));
    }

    [Fact]
    public void should_read_on_from_an_offset_and_end_without_a_next_offset()
    {
        var window = NoteWindow.Of(Long, 95_000, null, tail: false, around: null);

        window.Text.Should().HaveLength(5_000);
        (window.Offset, window.NextOffset).Should().Be((95_000, null));
    }

    [Fact]
    public void should_give_the_end_of_a_note_with_tail()
    {
        var window = NoteWindow.Of(Long, null, 1_000, tail: true, around: null);

        window.Text.Should().Be(Long[^1_000..]);
        (window.Offset, window.NextOffset, window.Truncated).Should().Be((99_000, null, true));
    }

    [Fact]
    public void should_give_the_passage_around_some_words_whatever_their_case()
    {
        var text = new string('x', 50_000) + "The Backup Folder is here" + new string('y', 50_000);

        var window = NoteWindow.Of(text, null, 2_000, tail: false, around: "backup folder");

        window.Text.Should().Contain("The Backup Folder is here");
        window.Offset.Should().BeGreaterThan(0).And.BeLessThan(50_000);
    }

    [Fact]
    public void should_start_at_the_beginning_when_the_words_are_not_there_and_keep_within_the_note()
    {
        NoteWindow.Of(Long, null, 10, tail: false, around: "not in it").Offset.Should().Be(0);
        NoteWindow.Of(Long, -5, 10, tail: false, around: null).Offset.Should().Be(0);
        NoteWindow.Of(Long, 500_000, 10, tail: false, around: null).Text.Should().BeEmpty();
    }

    [Fact]
    public void should_not_cut_a_character_in_two()
    {
        var text = new string('a', 9) + "😀" + new string('b', 20);

        var window = NoteWindow.Of(text, 0, 10, tail: false, around: null);

        window.Text.Should().Be(new string('a', 9));
        window.NextOffset.Should().Be(9);
    }
}
