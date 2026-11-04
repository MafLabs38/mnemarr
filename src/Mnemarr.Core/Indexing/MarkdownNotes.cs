using System.Text;

namespace Mnemarr.Core.Indexing;

/// <summary>A <c>[[wikilink]]</c> of a note: the name or path it points at (lower case, no extension, no heading, written from the root of the source), and its text if it has one.</summary>
public sealed record WikiLink(string Target, string? Alias);

/// <summary>A <c>[[link]]</c> or <c>![[embed]]</c> found in the text of a note: what is between the brackets, and whether it was an embed.</summary>
public sealed record WikiToken(string Inner, bool Embedded);

/// <summary>What a note says about itself and about other notes: the notes it links to, the files it embeds, and its tags.</summary>
public sealed record NoteMeta(IReadOnlyList<WikiLink> Links, IReadOnlyList<string> Embeds, IReadOnlyList<string> Tags)
{
    public static NoteMeta Empty { get; } = new([], [], []);

    /// <summary>
    /// The links as paths from the root of the source. <c>[[../01 - Topic A/Index]]</c>, written in the note <c>04 - Topic B/Basics.md</c>, points at
    /// <c>01 - topic a/index</c>: Obsidian reads it from the folder of the note. A link that climbs out of the source points at nothing and is dropped.
    /// </summary>
    public NoteMeta ResolvedFrom(string notePath)
    {
        var resolved = new List<WikiLink>();
        foreach (var link in Links)
        {
            if (MarkdownNotes.FromFolder(notePath, link.Target) is { } target)
            {
                resolved.Add(link with { Target = target });
            }
        }

        return this with { Links = resolved };
    }
}

/// <summary>
/// Reads the links and the tags of a Markdown note the way Obsidian does: <c>[[note]]</c>, <c>[[note|text]]</c>, <c>[[note#heading]]</c>,
/// <c>![[image.png]]</c>, <c>#tag</c> and the <c>tags</c> of the front matter. Code (fenced or inline) says nothing. A note is untrusted text,
/// so it is read by hand in one pass, with a bound on what it can make the index hold.
/// </summary>
public static class MarkdownNotes
{
    /// <summary>
    /// The version of the reading of links and tags, stored with each note. A note read by another version has its links and tags read again at the
    /// next pass, without being cut again: raise it with a change to how a link or a tag is read (1: the first reading).
    /// </summary>
    public const int Version = 1;

    private const int MaxItems = 200;
    private const int MaxLength = 200;

    /// <summary>The most characters between <c>[[</c> and <c>]]</c>: a target, a bar and an alias, each bounded.</summary>
    private const int MaxInner = (2 * MaxLength) + 10;
    private const int MaxFrontMatterLines = 100;
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    public static NoteMeta Parse(string markdown)
    {
        var text = markdown.TrimStart('\uFEFF');
        var (length, frontTags) = SplitFrontMatter(text);
        var body = text[length..];
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
                ReadLine(raw, links, embeds, tags);
            }
        }

        return new NoteMeta(Distinct(links, link => link.Target + "|" + link.Alias), [.. embeds.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxItems)], [.. tags.Distinct(StringComparer.Ordinal).Take(MaxItems)]);
    }

    /// <summary>The note without its front matter: what is rendered. A BOM is dropped as well.</summary>
    public static string Body(string markdown)
    {
        var text = markdown.TrimStart('\uFEFF');
        return text[SplitFrontMatter(text).Length..];
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
    private static (int Length, List<string> Tags) SplitFrontMatter(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal) || !(text.Length > 3 && text[3] is '\n' or '\r'))
        {
            return (0, []);
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
                return (position, tags);
            }

            inTags = ReadFrontMatterLine(line, inTags, tags);
        }

        // Never closed: it is not front matter, it is a line of dashes.
        return (0, []);
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

    /// <summary>One line of a note: the links, embeds and tags that are in it, code left out.</summary>
    private static void ReadLine(string raw, List<WikiLink> links, List<string> embeds, List<string> tags)
    {
        var line = WithoutInlineCode(raw);
        var tokens = FindTokens(line);
        foreach (var token in tokens)
        {
            AddLink(InnerOf(raw, token), token.Embedded, links, embeds);
        }

        ReadTags(line, tokens, tags);
    }

    /// <summary>Where a <c>[[link]]</c> or <c>![[embed]]</c> is in a line: the start (at the <c>!</c> of an embed) and the end, past the closing brackets.</summary>
    private readonly record struct Span(int Start, int End, bool Embedded);

    private static string InnerOf(string line, Span token) => line[(token.Start + (token.Embedded ? 3 : 2))..(token.End - 2)];

    /// <summary>
    /// The links and embeds of a line, left to right, in one pass. The next <c>]]</c> is remembered, and a link must close within what a link can
    /// hold: a line of brackets must not cost a search for each. A <c>[[</c> that never closes is text.
    /// </summary>
    private static List<Span> FindTokens(string line)
    {
        var tokens = new List<Span>();
        var index = 0;
        var nextClose = -2;
        while (index < line.Length - 1)
        {
            if (line[index] != '[' || line[index + 1] != '[')
            {
                index++;
                continue;
            }

            var from = index + 2;
            if (nextClose != -1 && nextClose < from)
            {
                nextClose = line.IndexOf("]]", from, StringComparison.Ordinal);
            }

            if (nextClose >= 0 && nextClose - from <= MaxInner)
            {
                var embedded = index > 0 && line[index - 1] == '!';
                tokens.Add(new Span(embedded ? index - 1 : index, nextClose + 2, embedded));
                index = nextClose + 2;
            }
            else
            {
                index += 2;
            }
        }

        return tokens;
    }

    private static void ReadTags(string line, List<Span> tokens, List<string> tags)
    {
        var next = 0;
        var index = 0;
        while (index < line.Length)
        {
            if (next < tokens.Count && index == tokens[next].Start)
            {
                index = tokens[next++].End;
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

    /// <summary>
    /// Rewrites the links and embeds of a note, outside code, with what <paramref name="replace"/> says (null leaves one as it was written):
    /// the same reading as the index's, so that what is shown and what is stored cannot disagree.
    /// </summary>
    public static string Rewrite(string body, Func<WikiToken, string?> replace)
    {
        var result = new StringBuilder(body.Length);
        var inFence = false;
        var first = true;
        foreach (var raw in body.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!first)
            {
                result.Append('\n');
            }

            first = false;
            if (raw.StartsWith("```", StringComparison.Ordinal) || raw.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                result.Append(raw);
            }
            else if (inFence)
            {
                result.Append(raw);
            }
            else
            {
                RewriteLine(raw, replace, result);
            }
        }

        return result.ToString();
    }

    private static void RewriteLine(string raw, Func<WikiToken, string?> replace, StringBuilder result)
    {
        var position = 0;
        foreach (var token in FindTokens(WithoutInlineCode(raw)))
        {
            result.Append(raw, position, token.Start - position);
            result.Append(replace(new WikiToken(InnerOf(raw, token), token.Embedded)) ?? raw[token.Start..token.End]);
            position = token.End;
        }

        result.Append(raw, position, raw.Length - position);
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

    /// <summary>The target and the text of what is between the brackets: <c>target|text</c>.</summary>
    public static (string Target, string? Alias) Split(string inner)
    {
        var bar = inner.IndexOf('|', StringComparison.Ordinal);
        return ((bar >= 0 ? inner[..bar] : inner).Trim(), bar >= 0 ? inner[(bar + 1)..].Trim() : null);
    }

    private static void AddLink(string inner, bool embedded, List<WikiLink> links, List<string> embeds)
    {
        var (target, alias) = Split(inner);
        if (embedded && ImageFormats.Is(FileOf(target)))
        {
            var file = FileOf(target);
            if (file.Length is > 0 and <= MaxLength && embeds.Count < MaxItems * 2)
            {
                embeds.Add(file);
            }

            return;
        }

        // Anything else that is embedded (`![[Other note]]`) is a note shown in place: for the index it is a link, and the embedding note is a backlink of it.
        if (NormalizeTarget(target) is { } normalized && links.Count < MaxItems * 2)
        {
            links.Add(new WikiLink(normalized, string.IsNullOrEmpty(alias) ? null : alias[..Math.Min(alias.Length, MaxLength)]));
        }
    }

    /// <summary>The file a link or an embed names: no heading, trimmed, with <c>/</c>.</summary>
    internal static string FileOf(string target)
    {
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        return (hash >= 0 ? target[..hash] : target).Trim().Replace('\\', '/');
    }

    private static bool IsTagChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '/';
}
