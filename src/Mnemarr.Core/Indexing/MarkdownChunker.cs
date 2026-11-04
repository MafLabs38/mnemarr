using System.Text;

namespace Mnemarr.Core.Indexing;

/// <summary>
/// Cuts a Markdown note into chunks that follow its headings. A section that is too long is cut again at paragraph
/// boundaries, then at a space. A heading inside a fenced code block is code, not a heading.
/// </summary>
public static class MarkdownChunker
{
    public const int DefaultMaxChars = 1200;

    /// <summary>
    /// The version of the cut. A note that was cut by another version is cut again at the next pass even if its content did not change, so
    /// that a change to the chunker (its size, how it reads a heading) reaches the notes that are already indexed. Raise it with the change (2: the links and tags of a note are read with it; 3: a link written from the folder of its note is stored from the root).
    /// </summary>
    public const int Version = 3;

    private const int MaxHeadingLevel = 6;

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

            if (!inFence && TryReadHeading(line, out var level, out var heading))
            {
                Flush(chunks, path.Current, section, maxChars);
                path.Enter(level, heading);
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

            if (!inFence && TryReadHeading(line, out var level, out var heading) && level == 1)
            {
                return heading;
            }
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>
    /// An ATX heading: one to six <c>#</c>, a space or a tab, the title, and optionally a closing run of <c>#</c> that is set apart by a
    /// space (so <c>## Learning C#</c> keeps its <c>#</c>). Read by hand, in one pass: a note is untrusted text, and a pattern with
    /// backtracking can be made to take cubic time on a line of spaces.
    /// </summary>
    private static bool TryReadHeading(string line, out int level, out string title)
    {
        level = 0;
        title = string.Empty;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is 0 or > MaxHeadingLevel || level >= line.Length || line[level] is not (' ' or '\t'))
        {
            return false;
        }

        var text = line.AsSpan(level).Trim([' ', '\t']);
        var end = text.TrimEnd('#');
        if (end.Length == 0 || (end.Length < text.Length && end[^1] is ' ' or '\t'))
        {
            text = end.TrimEnd([' ', '\t']);
        }

        if (text.Length == 0)
        {
            return false;
        }

        title = text.ToString();
        return true;
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
        // An offset, not a copy of the rest at each cut: one long paragraph costs a pass over it, not a pass per piece.
        var start = 0;
        while (paragraph.Length - start > maxChars)
        {
            var cut = paragraph.LastIndexOf(' ', start + maxChars - 1, maxChars);
            if (cut <= start)
            {
                cut = start + maxChars;
            }

            yield return paragraph[start..cut].TrimEnd();
            start = cut;
            while (start < paragraph.Length && char.IsWhiteSpace(paragraph[start]))
            {
                start++;
            }
        }

        yield return paragraph[start..];
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
