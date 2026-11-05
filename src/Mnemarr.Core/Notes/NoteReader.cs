using System.Text;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Notes;

/// <summary>
/// A note ready to be shown: the index's knowledge of it, its Markdown as written (without the front matter), and the same Markdown with its
/// wikilinks and embeds rewritten into links and images that lead where the caller may go (<see cref="WikiLinkRenderer"/>).
/// </summary>
public sealed record NoteContent(NoteDetail Detail, string Markdown, string Rendered, string Raw, string Version, SourceKind Kind, SourceMode Mode);

/// <summary>An image of a source, opened for reading, with the type to answer with.</summary>
public sealed record NoteFile(Stream Content, string ContentType);

/// <summary>
/// Reads a note and the images of a source for the reader (D-37, ADR 0017). Only what the index knows is read (a path that was not indexed is
/// not a file to read; an image is served only if the last pass found it), only inside the folder of its source and never through a link, and
/// the type of a file is decided by <see cref="ImageFormats"/>, never by the file: a short list of formats, and an SVG (which can carry a script) is not on it.
/// </summary>
public sealed class NoteReader
{
    /// <summary>The largest image served.</summary>
    public const long MaxImageBytes = 10 * 1024 * 1024;

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
        var bytes = ReadBytes(file);
        var text = Encoding.UTF8.GetString(bytes);
        var body = MarkdownNotes.Body(text);
        return new NoteContent(detail, body, new WikiLinkRenderer(_browser, sourceId, path, scope).Render(body), text, NoteWriter.VersionOf(bytes), source.Kind, source.Mode);
    }

    /// <summary>What a text would look like as a note of a source (the editor's preview): its links resolved as they will be once it is saved. The text is not kept.</summary>
    public string Preview(Guid sourceId, string path, string text, SourceScope scope)
    {
        _ = scope.CanAccess(sourceId) && _sources.Get(sourceId) is not null ? true : throw NoSuchNote();
        return new WikiLinkRenderer(_browser, sourceId, path, scope).Render(MarkdownNotes.Body(text.Length > MarkdownFileScanner.MaxFileBytes ? text[..(int)MarkdownFileScanner.MaxFileBytes] : text));
    }

    public NoteFile OpenImage(Guid sourceId, string path, SourceScope scope)
    {
        var source = _sources.Get(sourceId) is { } found && scope.CanAccess(sourceId) ? found : throw NoSuchFile();
        if (ImageFormats.ContentType(path) is not { } contentType || !_browser.HasImage(sourceId, path))
        {
            throw NoSuchFile();
        }

        var file = Resolve(source, path);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxImageBytes)
            {
                throw NoSuchFile();
            }

            var opened = stream;
            stream = null;
            return new NoteFile(opened, contentType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw NoSuchFile();
        }
        finally
        {
            stream?.Dispose();
        }
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

    private static byte[] ReadBytes(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(stream.Length, MarkdownFileScanner.MaxFileBytes + 1)];
            stream.ReadExactly(buffer);
            return buffer.Length > MarkdownFileScanner.MaxFileBytes ? throw NoSuchNote() : buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RejectedException(Problem.Conflict, "The note cannot be read now: try again in a moment.");
        }
    }

    private static RejectedException NoSuchNote() => new(Problem.NotFound, "There is no such note.");

    private static RejectedException NoSuchFile() => new(Problem.NotFound, "There is no such file.");
}
