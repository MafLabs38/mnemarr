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

    /// <summary>How many files of a source are looked at to find an embedded image by name: a source of this size is the limit of a walk at each read.</summary>
    private const int MaxFilesWalked = 50_000;

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
        return new NoteContent(detail, MarkdownNotes.Body(text), FindImages(source, path, MarkdownNotes.Parse(text).Embeds));
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

    /// <summary>
    /// Where the images that a note embeds (<c>![[plan.png]]</c>) are, by the name written in the note: as Obsidian finds them. Next to the note, or
    /// at the root of the source, or, failing that, anywhere in the source by file name (the shortest path wins), which one bounded walk of the
    /// source settles for all the embeds of the note. An embed that is not found is left out.
    /// </summary>
    private Dictionary<string, string> FindImages(Source source, string notePath, IEnumerable<string> embeds)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var embed in embeds.Where(embed => ImageTypes.ContainsKey(Path.GetExtension(embed))))
        {
            if (NextToTheNote(source, notePath, embed) is { } near)
            {
                found[embed] = near;
            }
            else
            {
                missing.Add(embed);
            }
        }

        if (missing.Count > 0)
        {
            FindByName(source, missing, found);
        }

        return found;
    }

    private string? NextToTheNote(Source source, string notePath, string embed)
    {
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
                // Not a place a note may read from: the embed leads nowhere from there.
            }
        }

        return null;
    }

    private void FindByName(Source source, List<string> missing, Dictionary<string, string> found)
    {
        string folder;
        try
        {
            folder = _paths.Allowed(source.Path);
        }
        catch (RejectedException)
        {
            return;
        }

        var wanted = missing.ToLookup(embed => Path.GetFileName(embed), StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options).Take(MaxFilesWalked).Where(file => wanted.Contains(file.Name)))
        {
            var relative = Path.GetRelativePath(folder, file.FullName).Replace('\\', '/');
            foreach (var embed in wanted[file.Name].Where(embed => string.Equals(relative, embed, StringComparison.OrdinalIgnoreCase) || relative.EndsWith('/' + embed, StringComparison.OrdinalIgnoreCase)))
            {
                if (!found.TryGetValue(embed, out var best) || relative.Length < best.Length)
                {
                    found[embed] = relative;
                }
            }
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
