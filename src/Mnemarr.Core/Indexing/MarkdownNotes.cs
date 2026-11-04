using System.Text;

namespace Mnemarr.Core.Indexing;

/// <summary>A <c>[[wikilink]]</c> of a note: the name or path it points at (lower case, no extension, no heading, written from the root of the source), and its text if it has one.</summary>
public sealed record WikiLink(string Target, string? Alias);

/// <summary>A <c>[[link]]</c> or <c>![[embed]]</c> found in the text of a note: what is between the brackets, and whether it was an embed.</summary>
public sealed record WikiToken(string Inner, bool Embedded);

/// <summary>What a <c>[[...]]</c> or <c>![[...]]</c> is: an embedded image (the file it names), a link to a note (its normalised target and its text), or nothing the index keeps.</summary>
public abstract record WikiKind
{
    public sealed record Image(string File) : WikiKind;

    public sealed record Note(string Target, string Text) : WikiKind;

    public sealed record Nothing : WikiKind;
}

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
            if (NotePaths.FromFolderTarget(notePath, link.Target) is { } target)
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
    internal const int MaxLength = 200;

    /// <summary>The most characters between <c>[[</c> and <c>]]</c>: a target, a bar and an alias, each bounded.</summary>
    private const int MaxInner = (2 * MaxLength) + 10;
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    public static NoteMeta Parse(string markdown)
    {
        var text = markdown.TrimStart('\uFEFF');
        var (length, frontTags) = FrontMatter.Split(text);
        var body = text[length..];
        var links = new List<WikiLink>();
        var embeds = new List<string>();
        var tags = new List<string>(frontTags);
        foreach (var (line, fenced) in Lines(body))
        {
            if (!fenced)
            {
                ReadLine(line, links, embeds, tags);
            }
        }

        return new NoteMeta(Distinct(links, link => link.Target + "|" + link.Alias), [.. embeds.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxItems)], [.. tags.Distinct(StringComparer.Ordinal).Take(MaxItems)]);
    }

    /// <summary>The lines of the body of a note, each with whether it is in a fenced code block (the lines of the fences included): what a fence holds is code, and says nothing.</summary>
    private static IEnumerable<(string Line, bool Fenced)> Lines(string body)
    {
        var inFence = false;
        foreach (var raw in body.ReplaceLineEndings("\n").Split('\n'))
        {
            if (raw.StartsWith("```", StringComparison.Ordinal) || raw.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                yield return (raw, true);
            }
            else
            {
                yield return (raw, inFence);
            }
        }
    }

    /// <summary>The note without its front matter: what is rendered. A BOM is dropped as well.</summary>
    public static string Body(string markdown)
    {
        var text = markdown.TrimStart('\uFEFF');
        return text[FrontMatter.Split(text).Length..];
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

    private static List<WikiLink> Distinct(List<WikiLink> links, Func<WikiLink, string> key) =>
        [.. links.DistinctBy(key, StringComparer.Ordinal).Take(MaxItems)];

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
    /// the same reading as the index's, so that what is shown and what is stored cannot disagree. <paramref name="replaceDestination"/>, if given,
    /// does the same for the destination of an ordinary Markdown link or image (<c>[text](destination)</c>).
    /// </summary>
    public static string Rewrite(string body, Func<WikiToken, string?> replace, Func<string, string?>? replaceDestination = null)
    {
        var result = new StringBuilder(body.Length);
        var first = true;
        foreach (var (line, fenced) in Lines(body))
        {
            if (!first)
            {
                result.Append('\n');
            }

            first = false;
            if (fenced)
            {
                result.Append(line);
            }
            else
            {
                RewriteLine(line, replace, replaceDestination, result);
            }
        }

        return result.ToString();
    }

    private static void RewriteLine(string raw, Func<WikiToken, string?> replace, Func<string, string?>? replaceDestination, StringBuilder result)
    {
        var masked = WithoutInlineCode(raw);
        var tokens = FindTokens(masked);
        var edits = tokens.Select(token => (token.Start, token.End, Text: replace(new WikiToken(InnerOf(raw, token), token.Embedded)))).ToList();
        if (replaceDestination is not null)
        {
            edits.AddRange(FindDestinations(masked, tokens).Select(span => (span.Start, span.End, Text: replaceDestination(raw[span.Start..span.End]))));
            edits.Sort((x, y) => x.Start.CompareTo(y.Start));
        }

        var position = 0;
        foreach (var (start, end, text) in edits)
        {
            if (start < position)
            {
                // Inside what was just written (a link inside the destination of another): left as it is.
                continue;
            }

            result.Append(raw, position, start - position);
            result.Append(text ?? raw[start..end]);
            position = end;
        }

        result.Append(raw, position, raw.Length - position);
    }

    /// <summary>
    /// The destinations of the ordinary Markdown links and images of a line (<c>](destination)</c> or <c>](destination "title")</c>), outside the
    /// wikilinks: bounded in length, one pass, so that a line of <c>](</c> does not cost more than what a destination can hold for each.
    /// </summary>
    private static List<Span> FindDestinations(string line, List<Span> tokens)
    {
        var found = new List<Span>();
        var inside = 0;
        var position = 0;
        while (position < line.Length)
        {
            var open = line.IndexOf("](", position, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            while (inside < tokens.Count && tokens[inside].End <= open)
            {
                inside++;
            }

            var from = open + 2;
            var end = DestinationEnd(line, from);
            var inWikilink = inside < tokens.Count && tokens[inside].Start <= open;
            if (end > from && !inWikilink)
            {
                found.Add(new Span(from, end, false));
                position = end;
            }
            else
            {
                position = from;
            }
        }

        return found;
    }

    /// <summary>Where the destination that starts at <paramref name="from"/> ends (at a <c>)</c> or a space), or -1 when it is empty, too long, or written between angle brackets.</summary>
    private static int DestinationEnd(string line, int from)
    {
        var end = from;
        while (end < line.Length && end - from <= MaxLength && line[end] is not (')' or ' ' or '\t'))
        {
            end++;
        }

        var closed = end < line.Length && line[end] is ')' or ' ' or '\t';
        return closed && end - from <= MaxLength && line[from] != '<' ? end : -1;
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

    /// <summary>
    /// What a token is, the one rule that the index and what is shown both follow: an embedded file of an image format is an image; anything else
    /// (a link, or a note embedded in place) is a link to a note if it names one; else nothing.
    /// </summary>
    public static WikiKind Classify(WikiToken token)
    {
        var (target, alias) = Split(token.Inner);
        var file = FileOf(target);
        if (token.Embedded && ImageFormats.Is(file))
        {
            return file.Length is > 0 and <= MaxLength ? new WikiKind.Image(file) : new WikiKind.Nothing();
        }

        if (NormalizeTarget(target) is not { } normalized)
        {
            return new WikiKind.Nothing();
        }

        var text = string.IsNullOrEmpty(alias) ? target : alias;
        return new WikiKind.Note(normalized, text);
    }

    private static void AddLink(string inner, bool embedded, List<WikiLink> links, List<string> embeds)
    {
        var (_, alias) = Split(inner);
        switch (Classify(new WikiToken(inner, embedded)))
        {
            case WikiKind.Image image when embeds.Count < MaxItems * 2:
                embeds.Add(image.File);
                break;
            case WikiKind.Note note when links.Count < MaxItems * 2:
                links.Add(new WikiLink(note.Target, string.IsNullOrEmpty(alias) ? null : alias[..Math.Min(alias.Length, MaxLength)]));
                break;
            default:
                break;
        }
    }

    /// <summary>The file a link or an embed names: no heading, trimmed, with <c>/</c>.</summary>
    internal static string FileOf(string target)
    {
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        return (hash >= 0 ? target[..hash] : target).Trim().Replace('\\', '/');
    }

    internal static bool IsTagChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '/';
}
