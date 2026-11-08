using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mnemarr.Core.Embeddings;

/// <summary>
/// The tokenizer of the model: a byte-level BPE read from the <c>tokenizer.json</c> of the model (the format of Hugging Face), with the same result as
/// the reference tokenizer for the text of a passage. It cuts the text with the expression the file gives, maps each byte to a printable letter, merges the
/// pairs in the order of the file, and wraps the ids in the start and end tokens. A special token written in the text is read as plain text, never as the token:
/// a passage cannot steer the model with the markers of its format.
/// </summary>
public sealed class ByteLevelBpeTokenizer
{
    private const int CacheLimit = 100_000;

    // The work of a text is bounded, whoever wrote it: a piece is cut at this many characters before it is merged (the merging of a piece is quadratic in its length),
    // a text is read only as far as the model can read (a token covers a few characters, rarely more than eight, so a limit of tokens is a limit of characters),
    // and only short pieces are kept for next time.
    private const int MaxPieceLength = 256;
    private const int CachedPieceLength = 32;
    private const int CharactersPerToken = 8;

    private static readonly string[] ByteLetters = BuildByteLetters();

    private readonly Dictionary<string, int> _vocabulary;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly Regex _pieces;
    private readonly ConcurrentDictionary<string, int[]> _cache = new(StringComparer.Ordinal);

    private ByteLevelBpeTokenizer(Dictionary<string, int> vocabulary, Dictionary<(string, string), int> ranks, Regex pieces, int start, int end, int pad)
    {
        _vocabulary = vocabulary;
        _ranks = ranks;
        _pieces = pieces;
        StartId = start;
        EndId = end;
        PadId = pad;
    }

    public int StartId { get; }

    public int EndId { get; }

    public int PadId { get; }

    /// <summary>Reads the tokenizer from the file of the model. A file that is not of the expected kind is refused, with what is missing.</summary>
    public static ByteLevelBpeTokenizer Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var model = root.GetProperty("model");
        if (model.GetProperty("type").GetString() != "BPE")
        {
            throw new InvalidDataException("The tokenizer is not a BPE tokenizer.");
        }

        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in model.GetProperty("vocab").EnumerateObject())
        {
            vocabulary[token.Name] = token.Value.GetInt32();
        }

        var merges = model.GetProperty("merges");
        var ranks = new Dictionary<(string, string), int>(merges.GetArrayLength());
        var rank = 0;
        foreach (var merge in merges.EnumerateArray())
        {
            ranks.TryAdd(PairOf(merge), rank++);
        }

        var (start, end) = Wrapping(root);
        return new ByteLevelBpeTokenizer(vocabulary, ranks, SplitExpression(root), start, end, PadOf(root, end));
    }

    /// <summary>The ids of a passage: the start token, the tokens of the text (cut at <paramref name="maxTokens"/> in all), the end token.</summary>
    public int[] Encode(string text, int maxTokens)
    {
        var body = new List<int>();
        var room = Math.Max(0, maxTokens - 2);
        if (text.Length > maxTokens * CharactersPerToken)
        {
            text = text[..(maxTokens * CharactersPerToken)];
        }

        foreach (var piece in Pieces(text))
        {
            if (body.Count >= room)
            {
                break;
            }

            for (var at = 0; at < piece.Length && body.Count < room; at += MaxPieceLength)
            {
                body.AddRange(Merged(piece.Length <= MaxPieceLength ? piece : piece.Substring(at, Math.Min(MaxPieceLength, piece.Length - at))));
            }
        }

        if (body.Count > room)
        {
            body.RemoveRange(room, body.Count - room);
        }

        var ids = new int[body.Count + 2];
        ids[0] = StartId;
        body.CopyTo(ids, 1);
        ids[^1] = EndId;
        return ids;
    }

    private static (string, string) PairOf(JsonElement merge)
    {
        if (merge.ValueKind == JsonValueKind.Array)
        {
            return (merge[0].GetString()!, merge[1].GetString()!);
        }

        var text = merge.GetString()!;
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return (text[..space], text[(space + 1)..]);
    }

    private static Regex SplitExpression(JsonElement root)
    {
        var pre = root.GetProperty("pre_tokenizer");
        var stages = pre.GetProperty("type").GetString() == "Sequence" ? pre.GetProperty("pretokenizers").EnumerateArray().ToList() : [pre];
        var split = stages.FirstOrDefault(stage => stage.GetProperty("type").GetString() == "Split");
        return split.ValueKind == JsonValueKind.Object
            ? new Regex(split.GetProperty("pattern").GetProperty("Regex").GetString()!, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(5))
            : throw new InvalidDataException("The tokenizer does not say how to cut a text into pieces.");
    }

    private static (int Start, int End) Wrapping(JsonElement root)
    {
        var template = root.GetProperty("post_processor");
        var specials = template.GetProperty("special_tokens");
        var ids = new List<int>();
        foreach (var item in template.GetProperty("single").EnumerateArray().Where(item => item.TryGetProperty("SpecialToken", out _)))
        {
            ids.Add(specials.GetProperty(item.GetProperty("SpecialToken").GetProperty("id").GetString()!).GetProperty("ids")[0].GetInt32());
        }

        return ids.Count == 2 ? (ids[0], ids[1]) : throw new InvalidDataException("The tokenizer does not wrap a text in a start and an end token.");
    }

    private static int PadOf(JsonElement root, int fallback) =>
        root.TryGetProperty("padding", out var padding) && padding.ValueKind == JsonValueKind.Object && padding.TryGetProperty("pad_id", out var id) && id.ValueKind == JsonValueKind.Number
            ? id.GetInt32()
            : fallback;

    private static string[] BuildByteLetters()
    {
        // The letters of the byte-level alphabet: the printable bytes stand for themselves, the others take the letters after 255, in order.
        var kept = new HashSet<int>(Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)));
        var letters = new string[256];
        var next = 256;
        for (var value = 0; value < 256; value++)
        {
            letters[value] = char.ConvertFromUtf32(kept.Contains(value) ? value : next++);
        }

        return letters;
    }

    private IEnumerable<string> Pieces(string text)
    {
        var at = 0;
        foreach (var match in _pieces.Matches(text).Cast<Match>())
        {
            if (match.Index > at)
            {
                yield return text[at..match.Index];
            }

            if (match.Length > 0)
            {
                yield return match.Value;
            }

            at = match.Index + match.Length;
        }

        if (at < text.Length)
        {
            yield return text[at..];
        }
    }

    private int[] Merged(string piece)
    {
        if (_cache.TryGetValue(piece, out var known))
        {
            return known;
        }

        var ids = MergeBytes(Encoding.UTF8.GetBytes(piece));
        if (piece.Length <= CachedPieceLength && _cache.Count < CacheLimit)
        {
            _cache[piece] = ids;
        }

        return ids;
    }

    private int[] MergeBytes(byte[] bytes)
    {
        var symbols = bytes.Select(value => ByteLetters[value]).ToList();
        while (symbols.Count > 1)
        {
            var best = int.MaxValue;
            var at = -1;
            for (var index = 0; index < symbols.Count - 1; index++)
            {
                if (_ranks.TryGetValue((symbols[index], symbols[index + 1]), out var rank) && rank < best)
                {
                    best = rank;
                    at = index;
                }
            }

            if (at < 0)
            {
                break;
            }

            var (left, right) = (symbols[at], symbols[at + 1]);
            for (var index = 0; index < symbols.Count - 1; index++)
            {
                if (symbols[index] == left && symbols[index + 1] == right)
                {
                    symbols[index] = left + right;
                    symbols.RemoveAt(index + 1);
                }
            }
        }

        // A symbol that the vocabulary does not hold (a byte that no text of its training used) is left out, as the reference tokenizer does with no unknown token.
        return [.. symbols.Where(_vocabulary.ContainsKey).Select(symbol => _vocabulary[symbol])];
    }
}
