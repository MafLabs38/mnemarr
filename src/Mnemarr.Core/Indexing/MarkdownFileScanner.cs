namespace Mnemarr.Core.Indexing;

/// <summary>A Markdown file of a source: its path from the root of the source, always with <c>/</c>, where it is, and its size and last write time when it was listed.</summary>
public sealed record ScannedFile(string RelativePath, string FullPath, long Length = 0, long ModifiedTicks = 0)
{
    /// <summary>The first folder of the path: the subject of a note in a vault organised by folders ("" at the root).</summary>
    public string Topic
    {
        get
        {
            var slash = RelativePath.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? string.Empty : RelativePath[..slash];
        }
    }
}

/// <summary>Walks a source and finds its notes. Hidden folders (<c>.git</c>, <c>.obsidian</c>) and links are not followed.</summary>
public static class MarkdownFileScanner
{
    /// <summary>A file larger than this is not a note.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>A file of a size a note can have. One that is removed between the listing and this look is not a note: the pass goes on.</summary>
    private static bool IsNote(FileInfo file)
    {
        try
        {
            return file.Length <= MaxFileBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The most images of a source that are recorded: past it, the first in path order are kept.</summary>
    public const int MaxImages = 50_000;

    /// <summary>The notes of a source, found lazily as the caller reads them; the images are known only once the walk is complete.</summary>
    public sealed class Walk
    {
        private readonly List<string> _images = [];

        internal Walk(string root)
        {
            Notes = Find(root, _images, () => Completed = true);
        }

        public IEnumerable<ScannedFile> Notes { get; }

        /// <summary>Whether every note was read to the end, so that <see cref="Images"/> is all of the images (in path order, at most <see cref="MaxImages"/>).</summary>
        public bool Completed { get; private set; }

        public IReadOnlyList<string> Images => Completed ? [.. _images.Order(StringComparer.Ordinal).Take(MaxImages)] : throw new InvalidOperationException("The walk is not complete.");
    }

    /// <summary>
    /// Walks a source once for its notes and its images (the image files a note may show, see <see cref="ImageFormats"/>; none hidden or behind a
    /// link): one walk, not two.
    /// </summary>
    public static Walk WalkOf(string root) => new(root);

    /// <summary>The notes of a source (no images): what a walk finds, read to the end.</summary>
    public static IEnumerable<ScannedFile> Scan(string root) => Find(root, null, null);

    private static IEnumerable<ScannedFile> Find(string root, List<string>? images, Action? completed)

    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            if (file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                if (IsNote(file))
                {
                    yield return new ScannedFile(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
                }
            }
            else if (images is not null && ImageFormats.Is(file.Name))
            {
                images.Add(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'));
            }
        }

        completed?.Invoke();
    }
}
