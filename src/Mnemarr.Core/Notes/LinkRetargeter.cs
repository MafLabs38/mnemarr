using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Notes;

/// <summary>
/// Rewrites the links of a note when a note is moved: the links that lead to the moved note (in the notes that hold them), and the relative
/// links of the moved note itself (so that they keep leading where they led). It reads links with the reader of the index
/// (<see cref="MarkdownNotes.Rewrite"/>): what is in code is left alone, and a link keeps the style it was written in (a bare name stays a bare
/// name, a path from the root stays one, a relative path stays relative), its heading and its text.
/// </summary>
internal static class LinkRetargeter
{
    /// <summary>The text of the note at <paramref name="notePath"/> with every link to <paramref name="oldPath"/> now leading to <paramref name="newPath"/>, and how many links that took.</summary>
    public static (string Text, int Changed) Retarget(string text, string notePath, string oldPath, string newPath)
    {
        var changed = 0;
        var oldTarget = Stored(oldPath);
        string? Token(WikiToken token)
        {
            var (target, alias) = MarkdownNotes.Split(token.Inner);
            var file = MarkdownNotes.FileOf(target);
            if ((token.Embedded && ImageFormats.Is(file)) || MarkdownNotes.NormalizeTarget(target) is not { } normalized
                || NotePaths.FromFolderTarget(notePath, normalized) is not { } resolved || !Leads(resolved, oldTarget))
            {
                return null;
            }

            changed++;
            var heading = target.Contains('#', StringComparison.Ordinal) ? target[target.IndexOf('#', StringComparison.Ordinal)..] : string.Empty;
            return Wiki(token.Embedded, Written(file, notePath, newPath) + heading, alias);
        }

        string? Destination(string destination)
        {
            var (address, tail) = SplitDestination(destination);
            if (!IsNote(address) || NotePaths.RelativePath(notePath, address) is not { } path || !string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            changed++;
            return RelativeAddress(notePath, newPath) + tail;
        }

        var result = Apply(text, Token, Destination);
        return (changed == 0 ? text : result, changed);
    }

    /// <summary>The text of a note that moves from <paramref name="oldNotePath"/> to <paramref name="newNotePath"/>, its relative links rewritten so that they lead to the same files.</summary>
    public static (string Text, int Changed) Rebase(string text, string oldNotePath, string newNotePath)
    {
        if (Folder(oldNotePath) == Folder(newNotePath))
        {
            return (text, 0);
        }

        var changed = 0;
        string? Token(WikiToken token)
        {
            var (target, alias) = MarkdownNotes.Split(token.Inner);
            var file = MarkdownNotes.FileOf(target);
            if (!(file.StartsWith("./", StringComparison.Ordinal) || file.StartsWith("../", StringComparison.Ordinal)) || NotePaths.RelativePath(oldNotePath, file) is not { } path)
            {
                return null;
            }

            changed++;
            var heading = target.Contains('#', StringComparison.Ordinal) ? target[target.IndexOf('#', StringComparison.Ordinal)..] : string.Empty;
            return Wiki(token.Embedded, DotRelative(newNotePath, path) + heading, alias);
        }

        string? Destination(string destination)
        {
            var (address, tail) = SplitDestination(destination);
            if (NotePaths.RelativePath(oldNotePath, address) is not { } path)
            {
                return null;
            }

            changed++;
            return RelativeAddress(newNotePath, path) + tail;
        }

        var result = Apply(text, Token, Destination);
        return (changed == 0 ? text : result, changed);
    }

    private static string Apply(string text, Func<WikiToken, string?> token, Func<string, string?> destination)
    {
        var bom = text.StartsWith('\uFEFF') ? "\uFEFF" : string.Empty;
        var rest = text[bom.Length..];
        var (length, _) = FrontMatter.Split(rest);
        var crlf = rest.Contains("\r\n", StringComparison.Ordinal);
        var body = MarkdownNotes.Rewrite(rest[length..], token, destination);
        return bom + rest[..length] + (crlf ? body.ReplaceLineEndings("\r\n") : body);
    }

    private static string Wiki(bool embedded, string target, string? alias) => (embedded ? "!" : string.Empty) + "[[" + target + (alias is null ? string.Empty : "|" + alias) + "]]";

    /// <summary>Whether a stored link (lower case, no extension) leads to the note: it is its path, or the end of it (a name, or the end of a path).</summary>
    private static bool Leads(string stored, string note) => stored == note || note.EndsWith("/" + stored, StringComparison.Ordinal);

    private static string Stored(string path) => WithoutExtension(path).ToLowerInvariant();

    private static string WithoutExtension(string path) => MarkdownNotes.MarkdownExtensions.FirstOrDefault(known => path.EndsWith(known, StringComparison.OrdinalIgnoreCase)) is { } extension ? path[..^extension.Length] : path;

    private static bool IsNote(string address) => MarkdownNotes.MarkdownExtensions.Any(known => address.EndsWith(known, StringComparison.OrdinalIgnoreCase));

    private static (string Address, string Tail) SplitDestination(string destination)
    {
        var end = destination.IndexOfAny(['#', '?', ' ']);
        return end < 0 ? (destination, string.Empty) : (destination[..end], destination[end..]);
    }

    private static string Folder(string notePath) => notePath.Contains('/', StringComparison.Ordinal) ? notePath[..notePath.LastIndexOf('/')] : string.Empty;

    /// <summary>What a link to the new note says, in the style the old link was written in.</summary>
    private static string Written(string file, string notePath, string newPath)
    {
        var withoutExtension = WithoutExtension(newPath);
        var extension = newPath[withoutExtension.Length..];
        string style;
        if (file.StartsWith("./", StringComparison.Ordinal) || file.StartsWith("../", StringComparison.Ordinal))
        {
            style = DotRelative(notePath, withoutExtension);
        }
        else
        {
            style = file.Contains('/', StringComparison.Ordinal) ? withoutExtension : withoutExtension[(withoutExtension.LastIndexOf('/') + 1)..];
        }

        return IsNote(file) ? style + extension : style;
    }

    /// <summary>A path from the folder of a note, starting with <c>./</c> or <c>../</c>: the form a wikilink is written in to be read from the note's folder.</summary>
    private static string DotRelative(string notePath, string target)
    {
        var relative = Path.GetRelativePath(Folder(notePath) is { Length: > 0 } folder ? folder : ".", target).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) ? relative : "./" + relative;
    }

    /// <summary>A path from the folder of a note, as the address of an ordinary link (spaces written as <c>%20</c>).</summary>
    private static string RelativeAddress(string notePath, string target) =>
        Path.GetRelativePath(Folder(notePath) is { Length: > 0 } folder ? folder : ".", target).Replace('\\', '/').Replace(" ", "%20", StringComparison.Ordinal);
}
