using System.Text.RegularExpressions;

namespace Mnemarr.Core.Titles;

/// <summary>
/// What a title and a summary may be, whoever wrote them (a person, a model): a line of plain words. They go into a note as its heading and under it, so
/// nothing in them may start a link, a tag or a heading of its own, or break the line. A model's answer is text that something else wrote.
/// </summary>
public static partial class TitleText
{
    public const int MaxTitle = 80;
    public const int MaxSummary = 400;

    /// <summary>A title, or null when nothing is left of what was given.</summary>
    public static string? Clean(string? text) => Line(text, MaxTitle, trimEnd: ".。");

    /// <summary>A summary, or null when there is none.</summary>
    public static string? CleanSummary(string? text) => Line(text, MaxSummary, trimEnd: string.Empty);

    private static string? Line(string? text, int max, string trimEnd)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var plain = Whitespace().Replace(Marks().Replace(text, string.Empty), " ").Trim().Trim('"', '\'', '`', '«', '»').Trim();
        plain = plain.Length > max ? plain[..max].TrimEnd() + "…" : plain;
        plain = plain.TrimEnd(trimEnd.ToCharArray());
        return plain.Length == 0 ? null : plain;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    // Brackets and hash marks are what make a link or a tag in a note; a leading "#" or ">" would make a heading or a quote.
    [GeneratedRegex(@"[\[\]#>]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Marks();
}
