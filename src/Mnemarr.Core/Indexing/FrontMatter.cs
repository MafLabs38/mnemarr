namespace Mnemarr.Core.Indexing;

/// <summary>
/// The front matter of a note (the block between two lines of <c>---</c> at its start, which Obsidian and Jekyll write): where it ends, and the
/// tags it declares (<c>tags: [a, b]</c>, <c>tags: a, b</c> or a list under <c>tags:</c>). A block that is never closed is not front matter.
/// </summary>
internal static class FrontMatter
{
    private const int MaxLines = 100;

    /// <summary>The length of the front matter block (including its fences and the line break after) and the tags it declares.</summary>
    internal static (int Length, List<string> Tags) Split(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal) || !(text.Length > 3 && text[3] is '\n' or '\r'))
        {
            return (0, []);
        }

        var tags = new List<string>();
        var position = text.IndexOf('\n', StringComparison.Ordinal) + 1;
        var inTags = false;
        for (var lines = 0; lines < MaxLines && position < text.Length; lines++)
        {
            var end = text.IndexOf('\n', position);
            end = end < 0 ? text.Length : end;
            var line = text[position..end].TrimEnd('\r');
            position = Math.Min(end + 1, text.Length);
            if (line is "---" or "...")
            {
                return (position, tags);
            }

            inTags = ReadLine(line, inTags, tags);
        }

        // Never closed: it is not front matter, it is a line of dashes.
        return (0, []);
    }

    /// <summary>One line of front matter; true when the following lines are the items of the <c>tags</c> list.</summary>
    private static bool ReadLine(string line, bool inTags, List<string> tags)
    {
        var trimmed = line.Trim();
        if (inTags && trimmed.StartsWith('-'))
        {
            AddTag(tags, trimmed[1..]);
            return true;
        }

        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || line[0] is ' ' or '\t')
        {
            return inTags && trimmed.Length == 0;
        }

        var key = line[..colon].Trim();
        if (key is not ("tags" or "tag"))
        {
            return false;
        }

        var value = line[(colon + 1)..].Trim().Trim('[', ']');
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            AddTag(tags, part);
        }

        return value.Length == 0;
    }

    private static void AddTag(List<string> tags, string text)
    {
        var tag = text.Trim().Trim('"', '\'').TrimStart('#').ToLowerInvariant();
        if (tag.Length is > 0 and <= MarkdownNotes.MaxLength && tag.All(MarkdownNotes.IsTagChar))
        {
            tags.Add(tag);
        }
    }

}
