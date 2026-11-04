using System.Text.RegularExpressions;

namespace Mnemarr.Core.Indexing;

/// <summary>What is asked of a search: words, and optionally one source and one topic (the first folder of a note).</summary>
public sealed record SearchRequest(string Query, Guid? SourceId = null, string? Topic = null, int Limit = 10);

/// <summary>A chunk that matches, with where it comes from. A higher score is a better match.</summary>
public sealed record SearchHit(
    Guid SourceId, string SourceName, string Path, string Topic, string Title, string Heading, string Snippet, double Score);

/// <summary>Turns what a person typed into a full-text query that cannot be anything but words.</summary>
internal static partial class FtsQuery
{
    private const int MaxWords = 16;

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    /// <summary>Every word, quoted, any of them: the best matches come first. Null when there is no word to look for.</summary>
    public static string? From(string text)
    {
        var words = Word().Matches(text).Select(match => $"\"{match.Value}\"").Distinct(StringComparer.Ordinal).Take(MaxWords).ToList();
        return words.Count == 0 ? null : string.Join(" OR ", words);
    }
}
