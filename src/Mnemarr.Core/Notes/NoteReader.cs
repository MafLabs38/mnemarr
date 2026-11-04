using System.Text;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Notes;

/// <summary>A note ready to be shown: the index's knowledge of it, its Markdown (without the front matter), and where the files it embeds are.</summary>
public sealed record NoteContent(NoteDetail Detail, string Markdown, IReadOnlyDictionary<string, string> Embeds);

/// <summary>An image of a source, opened for reading, with the type to answer with.</summary>
public sealed record NoteFile(Stream Content, string ContentType);

/// <summary>
/// Reads a note and the images of a source for the reader (D-37, ADR 0017). Only what the index knows is read (a path that was not indexed is
/// not a file to read), only inside the folder of its source and never through a link, and the type of a file is decided here, never
/// by the file: an image is one of a short list of formats, and an SVG (which can carry a script) is not on it.
/// </summary>
public sealed class NoteReader
{
    /// <summary>The largest image served.</summary>
    public const long MaxImageBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
    };

    private readonly ISourceStore _sources;
    private readonly INoteBrowser _browser;
    private readonly SourcePaths _paths;

    public NoteReader(ISourceStore sources, INoteBrowser browser, SourcePaths paths)
    {
        _sources = sources;
        _browser = browser;
        _paths = paths;
    }

    /// <summary>The notes the caller may read, optionally of one source or carrying one tag.</summary>
    public NoteListing List(SourceScope scope, Guid? sourceId, string? tag) => _browser.List(scope, sourceId, tag);

    /// <summary>The tags of the notes the caller may read.</summary>
    public TagListing Tags(SourceScope scope) => _browser.Tags(scope);

    public NoteContent Read(Guid sourceId, string path, SourceScope scope)
    {
        var detail = _browser.Find(sourceId, path, scope) ?? throw NoSuchNote();
        var source = _sources.Get(sourceId) ?? throw NoSuchNote();
        var file = Resolve(source, path);
        var text = ReadText(file);
        var embeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var embed in MarkdownNotes.Parse(text).Embeds)
        {
            if (FindImage(source, path, embed) is { } found)
            {
                embeds[embed] = found;
            }
        }

        return new NoteContent(detail, MarkdownNotes.Body(text), embeds);
    }

    public NoteFile OpenImage(Guid sourceId, string path, SourceScope scope)
    {
        var source = _sources.Get(sourceId) is { } found && scope.CanAccess(sourceId) ? found : throw NoSuchFile();
        if (!ImageTypes.TryGetValue(Path.GetExtension(path), out var contentType))
        {
            throw NoSuchFile();
        }

        var file = Resolve(source, path);
        try
        {
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxImageBytes)
            {
                stream.Dispose();
                throw NoSuchFile();
            }

            return new NoteFile(stream, contentType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw NoSuchFile();
        }
    }

    /// <summary>The image an embed (<c>![[plan.png]]</c>) names: next to the note, or at the root of the source. Null when there is none.</summary>
    private string? FindImage(Source source, string notePath, string embed)
    {
        if (!ImageTypes.ContainsKey(Path.GetExtension(embed)))
        {
            return null;
        }

        var folder = notePath.Contains('/', StringComparison.Ordinal) ? notePath[..notePath.LastIndexOf('/')] + "/" : string.Empty;
        foreach (var candidate in new[] { folder + embed, embed }.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (File.Exists(Resolve(source, candidate)))
                {
                    return candidate;
                }
            }
            catch (RejectedException)
            {
                // Not a place a note may read from: the embed leads nowhere.
            }
        }

        return null;
    }

    /// <summary>The full path of a file of a source, or a refusal: relative, inside the folder of the source, not hidden, not behind a link.</summary>
    private string Resolve(Source source, string relative)
    {
        var parts = relative.Split('/');
        if (relative.Length == 0 || relative.Contains('\0', StringComparison.Ordinal) || relative.Contains('\\', StringComparison.Ordinal) || Path.IsPathRooted(relative)
            || parts.Any(part => part.Length == 0 || part == ".." || part.StartsWith('.')))
        {
            throw NoSuchFile();
        }

        string folder;
        try
        {
            folder = _paths.Allowed(source.Path);
        }
        catch (RejectedException)
        {
            throw new RejectedException(Problem.Conflict, $"The folder of the source '{source.Name}' is not available.");
        }

        var full = Path.GetFullPath(Path.Combine(folder, relative));
        return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !SourcePaths.CrossesLink(full, folder)
            ? full
            : throw NoSuchFile();
    }

    private static string ReadText(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(stream.Length, MarkdownFileScanner.MaxFileBytes + 1)];
            stream.ReadExactly(buffer);
            return buffer.Length > MarkdownFileScanner.MaxFileBytes ? throw NoSuchNote() : Encoding.UTF8.GetString(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RejectedException(Problem.Conflict, "The note cannot be read now: try again in a moment.");
        }
    }

    private static RejectedException NoSuchNote() => new(Problem.NotFound, "There is no such note.");

    private static RejectedException NoSuchFile() => new(Problem.NotFound, "There is no such file.");
}
