using System.Text;
using System.Text.RegularExpressions;

namespace Mnemarr.Core.Indexing;

/// <summary>
/// Cuts a Markdown note into chunks that follow its headings. A section that is too long is cut again at paragraph
/// boundaries, then at a space. A heading inside a fenced code block is code, not a heading.
/// </summary>
public static partial class MarkdownChunker
{
    public const int DefaultMaxChars = 1200;

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$")]
    private static partial Regex HeadingPattern();

    public static IReadOnlyList<Chunk> Split(string markdown, int maxChars = DefaultMaxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        var chunks = new List<Chunk>();
        var path = new HeadingPath();
        var section = new StringBuilder();
        var inFence = false;

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (IsFence(line))
            {
                inFence = !inFence;
            }

            var heading = inFence ? Match.Empty : HeadingPattern().Match(line);
            if (heading.Success)
            {
                Flush(chunks, path.Current, section, maxChars);
                path.Enter(heading.Groups[1].Length, heading.Groups[2].Value);
                continue;
            }

            section.Append(line).Append('\n');
        }

        Flush(chunks, path.Current, section, maxChars);
        return chunks;
    }

    /// <summary>The title of a note: its first level-one heading, or the name of its file.</summary>
    public static string Title(string markdown, string fileName)
    {
        var inFence = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (IsFence(line))
            {
                inFence = !inFence;
            }

            var heading = inFence ? Match.Empty : HeadingPattern().Match(line);
            if (heading.Success && heading.Groups[1].Length == 1)
            {
                return heading.Groups[2].Value;
            }
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static bool IsFence(string line) =>
        line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal);

    private static void Flush(List<Chunk> chunks, string heading, StringBuilder section, int maxChars)
    {
        var text = section.ToString().Trim();
        section.Clear();
        if (text.Length == 0)
        {
            return;
        }

        chunks.AddRange(CutToSize(text, maxChars).Select(part => new Chunk(heading, part)));
    }

    private static IEnumerable<string> CutToSize(string text, int maxChars)
    {
        var current = new StringBuilder();
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var piece in HardCut(paragraph, maxChars))
            {
                if (current.Length > 0 && current.Length + piece.Length + 2 > maxChars)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                current.Append(current.Length > 0 ? "\n\n" : string.Empty).Append(piece);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static IEnumerable<string> HardCut(string paragraph, int maxChars)
    {
        var rest = paragraph;
        while (rest.Length > maxChars)
        {
            var cut = rest.LastIndexOf(' ', maxChars - 1, maxChars);
            if (cut <= 0)
            {
                cut = maxChars;
            }

            yield return rest[..cut].TrimEnd();
            rest = rest[cut..].TrimStart();
        }

        yield return rest;
    }

    /// <summary>The headings above the current line, as "A &gt; B &gt; C".</summary>
    private sealed class HeadingPath
    {
        private readonly List<string> _levels = [];

        public string Current => string.Join(" > ", _levels.Where(title => title.Length > 0));

        public void Enter(int level, string title)
        {
            while (_levels.Count >= level)
            {
                _levels.RemoveAt(_levels.Count - 1);
            }

            while (_levels.Count < level - 1)
            {
                _levels.Add(string.Empty);
            }

            _levels.Add(title.Trim());
        }
    }
}
