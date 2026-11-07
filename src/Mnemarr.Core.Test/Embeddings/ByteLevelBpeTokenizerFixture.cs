using System.Text.Json;
using Mnemarr.Core.Embeddings;

namespace Mnemarr.Core.Test.Embeddings;

public sealed class ByteLevelBpeTokenizerFixture : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"mnemarr-bpe-{Guid.NewGuid():N}");

    public ByteLevelBpeTokenizerFixture() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    /// <summary>A tokenizer of the same kind as the model's, small enough to read: the 256 bytes, a few merges, the cut of GPT-style models and two markers.</summary>
    private ByteLevelBpeTokenizer Tiny(string[] merges)
    {
        var kept = new HashSet<int>(Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)));
        var next = 256;
        var vocabulary = new Dictionary<string, int>();
        for (var value = 0; value < 256; value++)
        {
            vocabulary[char.ConvertFromUtf32(kept.Contains(value) ? value : next++)] = value;
        }

        var id = 256;
        foreach (var merge in merges)
        {
            vocabulary[merge.Replace(" ", string.Empty, StringComparison.Ordinal)] = id++;
        }

        var document = new
        {
            pre_tokenizer = new { type = "Split", pattern = new { Regex = @"\p{L}+|\p{N}+|\s+|[^\s\p{L}\p{N}]+" } },
            post_processor = new
            {
                single = new object[] { new { SpecialToken = new { id = "<s>" } }, new { Sequence = new { id = "A" } }, new { SpecialToken = new { id = "</s>" } } },
                special_tokens = new Dictionary<string, object> { ["<s>"] = new { ids = new[] { 900 } }, ["</s>"] = new { ids = new[] { 901 } } },
            },
            model = new { type = "BPE", vocab = vocabulary, merges },
        };
        var path = Path.Combine(_folder, $"tokenizer-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document));
        return ByteLevelBpeTokenizer.Load(path);
    }

    [Fact]
    public void should_merge_the_pairs_in_the_order_of_the_file_and_wrap_the_ids()
    {
        var subject = Tiny(["h e", "l l", "he ll", "hell o"]);

        subject.Encode("hello", 512).Should().Equal(900, 258 + 1, 901).And.NotBeEmpty("'hell' then 'o' is the 'hello' merge, the last of the file");
    }

    [Fact]
    public void should_cut_the_pieces_and_keep_the_spaces_as_tokens_of_their_own()
    {
        var subject = Tiny(["h i"]);

        // 256 is the merge 'hi'; 32 is the byte of the space.
        subject.Encode("hi hi", 512).Should().Equal(900, 256, 32, 256, 901);
    }

    [Fact]
    public void should_read_the_bytes_of_a_letter_that_is_not_ascii_one_by_one_when_no_merge_joins_them()
    {
        var subject = Tiny([]);

        // "é" is two bytes in UTF-8: two tokens, plus the start and the end.
        subject.Encode("é", 512).Should().HaveCount(4);
    }

    [Fact]
    public void should_read_the_marker_of_a_format_written_in_a_text_as_plain_text()
    {
        var subject = Tiny([]);

        subject.Encode("<s>", 512).Should().Equal(900, (int)'<', (int)'s', (int)'>', 901);
    }

    [Fact]
    public void should_cut_a_long_text_so_that_the_wrapping_stays_inside_the_limit()
    {
        var subject = Tiny([]);

        var ids = subject.Encode(new string('a', 100), 10);

        ids.Should().HaveCount(10);
        ids[0].Should().Be(900);
        ids[^1].Should().Be(901);
    }

    [Fact]
    public void should_refuse_a_file_that_is_not_a_byte_level_tokenizer()
    {
        var path = Path.Combine(_folder, "wrong.json");
        File.WriteAllText(path, """{"model":{"type":"WordPiece","vocab":{},"merges":[]}}""");

        var load = () => ByteLevelBpeTokenizer.Load(path);

        load.Should().Throw<InvalidDataException>();
    }
}
