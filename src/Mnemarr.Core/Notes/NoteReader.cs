using System.Text;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Notes;

/// <summary>
/// A note ready to be shown: the index's knowledge of it, its Markdown as written (without the front matter), and the same Markdown with its
/// wikilinks and embeds rewritten into links and images that lead where the caller may go (<see cref="WikiLinkRenderer"/>).
/// A note longer than a page of the interface comes in pages: <c>Page</c> (from 0) of <c>Pages</c>, each cut at a line end; the version is the one of the whole note. A caller that reads for an assistant asks for the whole.
/// </summary>
public sealed record NoteContent(NoteDetail Detail, string Markdown, string Rendered, string Raw, string Version, SourceKind Kind, SourceMode Mode, int Page = 0, int Pages = 1)
{
    /// <summary>The text is one page of a longer note, not the note.</summary>
    public bool Cut => Pages > 1;
}

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
    private readonly long _maxNoteBytes;

    /// <summary>The longest text that the interface is given at once for one note: a longer note comes in pages (a page is drawn in about a second), and an assistant reads it whole, in windows.</summary>
    public const int PageChars = 512 * 1024;

    public NoteReader(ISourceStore sources, INoteBrowser browser, SourcePaths paths, IOptions<IndexingOptions>? options = null)
    {
        _maxNoteBytes = options?.Value.MaxNoteBytes ?? IndexingOptions.DefaultMaxNoteBytes;
        _sources = sources;
        _browser = browser;
        _paths = paths;
    }

    /// <summary>The notes the caller may read, optionally of one source or carrying one tag.</summary>
    public NoteListing List(SourceScope scope, Guid? sourceId, string? tag) => _browser.List(scope, sourceId, tag);

    /// <summary>The tags of the notes the caller may read.</summary>
    public TagListing Tags(SourceScope scope) => _browser.Tags(scope);

    /// <summary>The note, ready to show.</summary>
    /// <param name="sourceId">The source of the note.</param>
    /// <param name="path">The path of the note in its source.</param>
    /// <param name="scope">What the caller may see.</param>
    /// <param name="whole">Give the whole text of a long note, as <c>Raw</c> only (an assistant reads it in windows: nothing is drawn, so <c>Markdown</c> and <c>Rendered</c> are empty); otherwise a note longer than <see cref="PageChars"/> comes in pages cut at a line end.</param>
    /// <param name="page">The page to give, from 0, of a note that comes in pages; one past the last is the last.</param>
    public NoteContent Read(Guid sourceId, string path, SourceScope scope, bool whole = false, int page = 0)
    {
        var detail = _browser.Find(sourceId, path, scope) ?? throw NoSuchNote();
        var source = _sources.Get(sourceId) ?? throw NoSuchNote();
        var file = Resolve(source, path);
        using var turn = LargeRead.Enter(file);
        var bytes = ReadBytes(file);
        var text = Encoding.UTF8.GetString(bytes);
        if (whole)
        {
            return new NoteContent(detail, string.Empty, string.Empty, text, NoteWriter.VersionOf(bytes), source.Kind, source.Mode);
        }

        var pages = 1;
        if (text.Length > PageChars)
        {
            var starts = PageStarts(text);
            pages = starts.Count;
            page = Math.Clamp(page, 0, pages - 1);
            text = text[starts[page]..(page + 1 < pages ? starts[page + 1] : text.Length)];
        }
        else
        {
            page = 0;
        }

        var body = MarkdownNotes.Body(text);
        // A page is not the note: its version is its own, which no file has, so that what is read as a page can never be sent back as the note (ADR 0043).
        var version = pages > 1 ? $"{NoteWriter.VersionOf(bytes)}-page-{page}" : NoteWriter.VersionOf(bytes);
        return new NoteContent(detail, body, new WikiLinkRenderer(_browser, sourceId, path, scope).Render(body), text, version, source.Kind, source.Mode, page, pages);
    }

    /// <summary>Where each page begins: about <see cref="PageChars"/> characters on, back to the end of a line so that no line is split (a line longer than a page is split where the page ends, never inside a surrogate pair).</summary>
    private static List<int> PageStarts(string text)
    {
        var starts = new List<int> { 0 };
        while (text.Length - starts[^1] > PageChars)
        {
            var limit = starts[^1] + PageChars;
            var end = text.LastIndexOf('\n', limit - 1, PageChars);
            var next = end >= 0 ? end + 1 : limit;
            if (next == limit && char.IsHighSurrogate(text[next - 1]))
            {
                next--;
            }

            starts.Add(next);
        }

        return starts;
    }

    /// <summary>What a text would look like as a note of a source (the editor's preview): its links resolved as they will be once it is saved. The text is not kept.</summary>
    public string Preview(Guid sourceId, string path, string text, SourceScope scope)
    {
        _ = _sources.Get(sourceId) is { } known && scope.CanSee(known) ? true : throw NoSuchNote();
        return new WikiLinkRenderer(_browser, sourceId, path, scope).Render(MarkdownNotes.Body(text.Length > MarkdownFileScanner.MaxFileBytes ? text[..(int)MarkdownFileScanner.MaxFileBytes] : text));
    }

    public NoteFile OpenImage(Guid sourceId, string path, SourceScope scope)
    {
        var source = _sources.Get(sourceId) is { } found && scope.CanSee(found) ? found : throw NoSuchFile();
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
            folder = _paths.AllowedFor(source);
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

    private byte[] ReadBytes(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(stream.Length, _maxNoteBytes + 1)];
            stream.ReadExactly(buffer);
            return buffer.Length > _maxNoteBytes ? throw new RejectedException(Problem.Conflict, "This note is above the size limit (Indexing:MaxNoteMegabytes): it is not read. The search still has what was indexed before.") : buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RejectedException(Problem.Conflict, "The note cannot be read now: try again in a moment.");
        }
    }

    /// <summary>
    /// A note of several megabytes is read whole, decoded, cut and drawn: a few readers of the biggest notes at once are the memory of a small host (ADR 0043). A read of such a note takes one of
    /// <see cref="AtOnce"/> places for its whole length; a reader who finds none for <see cref="Waits"/> (a thread of the host is held that long at most) is told to try again after as long.
    /// </summary>
    private sealed class LargeRead : IDisposable
    {
        private const int AtOnce = 2;
        private static readonly TimeSpan Waits = TimeSpan.FromSeconds(1);
        private static readonly SemaphoreSlim Places = new(AtOnce);
        private readonly bool _holding;

        private LargeRead(bool holding)
        {
            _holding = holding;
        }

        public static LargeRead Enter(string file)
        {
            if (!IsLarge(file))
            {
                return new LargeRead(holding: false);
            }

            return Places.Wait(Waits)
                ? new LargeRead(holding: true)
                : throw new RejectedException(Problem.Busy, "Other long notes are being read: try again in a moment.", Waits);
        }

        public void Dispose()
        {
            if (_holding)
            {
                Places.Release();
            }
        }

        private static bool IsLarge(string file)
        {
            try
            {
                return new FileInfo(file).Length > MarkdownFileScanner.MaxFileBytes;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    private static RejectedException NoSuchNote() => new(Problem.NotFound, "There is no such note.");

    private static RejectedException NoSuchFile() => new(Problem.NotFound, "There is no such file.");
}
