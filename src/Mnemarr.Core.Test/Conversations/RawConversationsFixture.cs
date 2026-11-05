using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Test.Conversations;

public sealed class RawConversationsFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly RawConversations _raw;

    public RawConversationsFixture()
    {
        _raw = new RawConversations(_workspace.Options);
    }

    public void Dispose() => _workspace.Dispose();

    private string Folder => Path.Combine(_workspace.DataFolder, "raw", "pc", "s1");

    [Fact]
    public void should_make_a_conversation_that_has_many_batches_one_file_and_read_every_line_of_it_still()
    {
        for (var position = 0; position < RawConversations.CompactAfter; position++)
        {
            _raw.Write("pc", "s1", position, [$"line {position}"]);
        }

        _raw.CompactIfNeeded("pc", "s1", RawConversations.CompactAfter);

        Directory.EnumerateFiles(Folder, "*.jsonl.gz").Should().ContainSingle();
        _raw.Read("pc", "s1", RawConversations.CompactAfter).Should().Equal(Enumerable.Range(0, RawConversations.CompactAfter).Select(position => $"line {position}"));
    }

    [Fact]
    public void should_go_on_reading_after_a_compaction_that_was_cut_short_left_old_files_behind()
    {
        _raw.Write("pc", "s1", 0, ["a", "b"]);
        _raw.Write("pc", "s1", 2, ["c"]);
        // A compaction that wrote the first file and crashed before it removed the others.
        _raw.Write("pc", "s1", 0, ["a", "b", "c"]);
        _raw.Write("pc", "s1", 3, ["d"]);

        _raw.Read("pc", "s1", 4).Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public void should_not_read_a_batch_that_was_written_for_a_position_never_taken_nor_past_the_count_asked()
    {
        _raw.Write("pc", "s1", 0, ["a", "b"]);
        _raw.Write("pc", "s1", 5, ["far"]);

        _raw.Read("pc", "s1", 6).Should().Equal("a", "b");
        _raw.Read("pc", "s1", 1).Should().Equal("a");
    }
}
