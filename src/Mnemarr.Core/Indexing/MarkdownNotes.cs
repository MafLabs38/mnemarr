using System.Text;

namespace Mnemarr.Core.Indexing;

/// <summary>A <c>[[wikilink]]</c> of a note: the name or path it points at (lower case, no extension, no heading, written from the root of the source), and its text if it has one.</summary>
public sealed record WikiLink(string Target, string? Alias, string? Written = null)
{
    /// <summary>The target as the note wrote it (normalised, not resolved from its folder): what the interface finds in the text of the note and looks up.</summary>
    public string AsWritten => Written ?? Target;
}

/// <summary>What a note says about itself and about other notes: the notes it links to, the files it embeds, and its tags.</summary>
public sealed record NoteMeta(IReadOnlyList<WikiLink> Links, IReadOnlyList<string> Embeds, IReadOnlyList<string> Tags)
{
    public static NoteMeta Empty { get; } = new([], [], []);

    /// <summary>
    /// The links as paths from the root of the source. <c>[[../01 - Java/Index]]</c>, written in the note <c>04 - Angular/Basics.md</c>, points at
    /// <c>01 - java/index</c>: Obsidian reads it from the folder of the note. A link that climbs out of the source points at nothing and is dropped.
    /// </summary>
    public NoteMeta ResolvedFrom(string notePath) => this with { Links = [.. Links.Select(link => (link, MarkdownNotes.FromFolder(notePath, link.Target))).Where(pair => pair.Item2 is not null).Select(pair => pair.link with { Target = pair.Item2!, Written = pair.link.AsWritten })] };
}

/// <summary>
/// Reads the links and the tags of a Markdown note the way Obsidian does: <c>[[note]]</c>, <c>[[note|text]]</c>, <c>[[note#heading]]</c>,
/// <c>![[image.png]]</c>, <c>#tag</c> and the <c>tags</c> of the front matter. Code (fenced or inline) says nothing. A note is untrusted text,
/// so it is read by hand in one pass, with a bound on what it can make the index hold.
/// </summary>
public static class MarkdownNotes
{
    private const int MaxItems = 200;
    private const int MaxLength = 200;
    private const int MaxFrontMatterLines = 100;
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    public static NoteMeta Parse(string markdown)
    {
        var (_, frontTags) = SplitFrontMatter(markdown);
        var body = Body(markdown);
        var links = new List<WikiLink>();
        var embeds = new List<string>();
        var tags = new List<string>(frontTags);
        var inFence = false;
        foreach (var raw in body.ReplaceLineEndings("\n").Split('\n'))
        {
            if (raw.StartsWith("```", StringComparison.Ordinal) || raw.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence)
            {
                ReadLine(WithoutInlineCode(raw), links, embeds, tags);
            }
        }

        return new NoteMeta(Distinct(links, link => link.Target + "|" + link.Alias), [.. embeds.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxItems)], [.. tags.Distinct(StringComparer.Ordinal).Take(MaxItems)]);
    }

    /// <summary>The note without its front matter: what is rendered. A BOM is dropped as well.</summary>
    public static string Body(string markdown)
    {
        var text = markdown.TrimStart('\uFEFF');
        var (length, _) = SplitFrontMatter(text);
        return text[length..];
    }

    /// <summary>The target of a link as it is stored and compared: no heading, no extension, trimmed, lower case. Null when there is nothing to point at.</summary>
    public static string? NormalizeTarget(string target)
    {
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var name = (hash >= 0 ? target[..hash] : target).Trim().Replace('\\', '/').Trim('/');
        foreach (var extension in MarkdownExtensions.Where(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            name = name[..^extension.Length];
        }

        return name.Length is 0 or > MaxLength ? null : name.ToLowerInvariant();
    }

    /// <summary>The target of a link written from the folder of a note: unchanged when it is a name or a path from the root, resolved when it starts with <c>./</c> or <c>../</c>, null when it leaves the source.</summary>
    internal static string? FromFolder(string notePath, string target)
    {
        if (!target.StartsWith("./", StringComparison.Ordinal) && !target.StartsWith("../", StringComparison.Ordinal))
        {
            return target;
        }

        var parts = notePath.Split('/').SkipLast(1).ToList();
        foreach (var part in target.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0 ? null : string.Join('/', parts).ToLowerInvariant();
    }

    private static List<WikiLink> Distinct(List<WikiLink> links, Func<WikiLink, string> key) =>
        [.. links.DistinctBy(key, StringComparer.Ordinal).Take(MaxItems)];

    /// <summary>The length of the front matter block (including its fences and the line break after) and the tags it declares.</summary>
    private static (int Length, List<string> Tags) SplitFrontMatter(string markdown)
    {
        var text = markdown.TrimStart('\uFEFF');
        var offset = markdown.Length - text.Length;
        if (!text.StartsWith("---", StringComparison.Ordinal) || !(text.Length > 3 && text[3] is '\n' or '\r'))
        {
            return (offset, []);
        }

        var tags = new List<string>();
        var position = text.IndexOf('\n', StringComparison.Ordinal) + 1;
        var inTags = false;
        for (var lines = 0; lines < MaxFrontMatterLines && position < text.Length; lines++)
        {
            var end = text.IndexOf('\n', position);
            end = end < 0 ? text.Length : end;
            var line = text[position..end].TrimEnd('\r');
            position = Math.Min(end + 1, text.Length);
            if (line is "---" or "...")
            {
                return (offset + position, tags);
            }

            inTags = ReadFrontMatterLine(line, inTags, tags);
        }

        // Never closed: it is not front matter, it is a line of dashes.
        return (offset, []);
    }

    /// <summary>One line of front matter; true when the following lines are the items of the <c>tags</c> list.</summary>
    private static bool ReadFrontMatterLine(string line, bool inTags, List<string> tags)
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
        if (tag.Length is > 0 and <= MaxLength && tag.All(IsTagChar))
        {
            tags.Add(tag);
        }
    }

    private static string WithoutInlineCode(string line)
    {
        if (!line.Contains('`', StringComparison.Ordinal))
        {
            return line;
        }

        var result = new StringBuilder(line.Length);
        var inCode = false;
        foreach (var c in line)
        {
            if (c == '`')
            {
                inCode = !inCode;
                result.Append(' ');
                continue;
            }

            result.Append(inCode ? ' ' : c);
        }

        return result.ToString();
    }

    private static void ReadLine(string line, List<WikiLink> links, List<string> embeds, List<string> tags)
    {
        var index = 0;
        while (index < line.Length)
        {
            if (line.AsSpan(index).StartsWith("[["))
            {
                var close = line.IndexOf("]]", index + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    // Never closed: it is text, and what follows on the line (a tag) is still read.
                    index += 2;
                    continue;
                }

                AddLink(line[(index + 2)..close], embedded: index > 0 && line[index - 1] == '!', links, embeds);
                index = close + 2;
            }
            else if (StartsTag(line, index))
            {
                index = ReadTag(line, index, tags);
            }
            else
            {
                index++;
            }
        }
    }

    /// <summary>A <c>#</c> that opens a tag: at the start of the line, after a space or after <c>[</c> (so not <c>C#</c>, not the anchor of a link such as <c>(#setup)</c>).</summary>
    private static bool StartsTag(string line, int index) =>
        line[index] == '#' && (index == 0 || char.IsWhiteSpace(line[index - 1]) || line[index - 1] is '[');

    /// <summary>Reads the tag that starts at <paramref name="index"/> (if it is one: a number, or a heading's <c>#</c>, is not) and returns where the line goes on.</summary>
    private static int ReadTag(string line, int index, List<string> tags)
    {
        var end = index + 1;
        while (end < line.Length && IsTagChar(line[end]))
        {
            end++;
        }

        var tag = line[(index + 1)..end].ToLowerInvariant();
        if (tag.Length is > 0 and <= MaxLength && tag.Any(char.IsLetter) && tags.Count < MaxItems)
        {
            tags.Add(tag);
        }

        return end;
    }

    private static void AddLink(string inner, bool embedded, List<WikiLink> links, List<string> embeds)
    {
        var bar = inner.IndexOf('|', StringComparison.Ordinal);
        var target = (bar >= 0 ? inner[..bar] : inner).Trim();
        var alias = bar >= 0 ? inner[(bar + 1)..].Trim() : null;
        if (embedded)
        {
            var hash = target.IndexOf('#', StringComparison.Ordinal);
            var file = (hash >= 0 ? target[..hash] : target).Trim().Replace('\\', '/');
            if (file.Length is > 0 and <= MaxLength)
            {
                embeds.Add(file);
            }

            return;
        }

        if (NormalizeTarget(target) is { } normalized && links.Count < MaxItems * 2)
        {
            links.Add(new WikiLink(normalized, string.IsNullOrEmpty(alias) ? null : alias[..Math.Min(alias.Length, MaxLength)]));
        }
    }

    private static bool IsTagChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '/';
}
