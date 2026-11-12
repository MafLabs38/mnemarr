using System.Text.RegularExpressions;

namespace Mnemarr.Core.Indexing;

/// <summary>
/// What is asked of a search: words, and optionally one source, topics (the first folder of a note) to stay within or to leave out, sources to leave out, and how many
/// hits one note may give (ADR 0043: a conversation of a thousand passages must not fill the answer by itself).
/// </summary>
public sealed record SearchRequest(string Query, Guid? SourceId = null, string? Topic = null, int Limit = 10)
{
    /// <summary>The most hits a search gives, and the longest list that the index gives for one question: the search by meaning merges lists of this length.</summary>
    public const int MaxLimit = 50;

    /// <summary>How many hits one note gives when nothing else is asked.</summary>
    public const int DefaultPerNote = 3;

    /// <summary>More topics to stay within, besides <see cref="Topic"/>: a note is kept when its topic is any one of them.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>Topics to leave out.</summary>
    public IReadOnlyList<string> ExcludeTopics { get; init; } = [];

    /// <summary>Sources to leave out.</summary>
    public IReadOnlyList<Guid> ExcludeSources { get; init; } = [];

    /// <summary>The most hits that one note gives, 1 to <see cref="MaxLimit"/>.</summary>
    public int PerNote { get; init; } = DefaultPerNote;

    /// <summary>The topics to stay within, <see cref="Topic"/> included; empty when the search is not limited to topics.</summary>
    public IReadOnlyList<string> StayWithin => [.. (Topic is null ? Topics : Topics.Prepend(Topic)).Where(topic => topic.Length > 0).Distinct(StringComparer.Ordinal)];
}

/// <summary>A chunk that matches, with where it comes from. A higher score is a better match.</summary>
public sealed record SearchHit(
    Guid SourceId, string SourceName, string Path, string Topic, string Title, string Heading, string Snippet, double Score, long ChunkId = 0, string Match = SearchMatch.Keywords);

/// <summary>Why a chunk is in the answer: it holds the words, it means what was asked, or both.</summary>
public static class SearchMatch
{
    public const string Keywords = "keywords";
    public const string Meaning = "meaning";
    public const string Both = "both";
}

/// <summary>Turns what a person typed into a full-text query that cannot be anything but words.</summary>
internal static partial class FtsQuery
{
    private const int MaxWords = 16;

    // The small words of a question (French and English) are in nearly every note: as search terms they match everything and bury the notes that answer.
    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "le", "la", "les", "un", "une", "des", "du", "de", "d", "l", "et", "ou", "à", "a", "au", "aux", "en", "dans", "sur", "sous", "par", "pour", "avec", "sans", "ce", "cet", "cette",
        "ces", "mon", "ma", "mes", "ton", "ta", "tes", "son", "sa", "ses", "qui", "que", "quoi", "dont", "où", "est", "sont", "être", "avoir", "il", "elle", "on", "je", "tu", "nous",
        "vous", "ils", "elles", "ne", "pas", "se", "y", "comment", "pourquoi", "quand", "quel", "quelle", "quels", "quelles", "the", "an", "of", "to", "in", "on", "at", "for", "and",
        "or", "is", "are", "was", "were", "be", "it", "this", "that", "with", "by", "from", "as", "how", "why", "what", "when", "which", "who", "do", "does", "my", "your", "i",
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    /// <summary>How many words of a text count as search terms: the small words of a question are not counted.</summary>
    public static int Meaningful(string text) => Word().Matches(text).Select(match => match.Value).Count(word => !SmallWords.Contains(word));

    /// <summary>Every word, quoted, any of them: the best matches come first. Null when there is no word to look for.</summary>
    public static string? From(string text)
    {
        var found = Word().Matches(text).Select(match => match.Value).ToList();
        // A question made only of small words is searched as it is; otherwise the small words are left out.
        var meaningful = found.Where(word => !SmallWords.Contains(word)).ToList();
        var words = (meaningful.Count > 0 ? meaningful : found).Select(word => $"\"{word}\"").Distinct(StringComparer.Ordinal).Take(MaxWords).ToList();
        return words.Count == 0 ? null : string.Join(" OR ", words);
    }
}
