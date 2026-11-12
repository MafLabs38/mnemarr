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
    /// <summary>The most that a person writes into a note in one go, and the part of a long note that the interface shows (what is indexed and read is <see cref="IndexingOptions.MaxNoteBytes"/>, ADR 0043).</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>The size of a file, or null when it was removed between the listing and this look: the pass goes on.</summary>
    private static long? LengthOf(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The most images of a source that are recorded: past it, the first in path order are kept.</summary>
    public const int MaxImages = 50_000;

    /// <summary>The notes of a source, found lazily as the caller reads them; the images are known only once the walk is complete.</summary>
    public sealed class Walk
    {
        private readonly List<string> _images = [];
        private readonly List<string> _tooLarge = [];

        internal Walk(string root, long maxNoteBytes)
        {
            Notes = Find(root, maxNoteBytes, _images, _tooLarge, () => Completed = true);
        }

        /// <summary>The notes that are larger than the limit: not read, but not forgotten either (known once the walk is complete).</summary>
        public IReadOnlyList<string> TooLarge => Completed ? _tooLarge : throw new InvalidOperationException("The walk is not complete.");

        public IEnumerable<ScannedFile> Notes { get; }

        /// <summary>Whether every note was read to the end, so that <see cref="Images"/> is all of the images (in path order, at most <see cref="MaxImages"/>).</summary>
        public bool Completed { get; private set; }

        public IReadOnlyList<string> Images => Completed ? [.. _images.Order(StringComparer.Ordinal).Take(MaxImages)] : throw new InvalidOperationException("The walk is not complete.");
    }

    /// <summary>
    /// Walks a source once for its notes and its images (the image files a note may show, see <see cref="ImageFormats"/>; none hidden or behind a
    /// link): one walk, not two.
    /// </summary>
    public static Walk WalkOf(string root, long maxNoteBytes = IndexingOptions.DefaultMaxNoteMegabytes * 1024L * 1024L) => new(root, maxNoteBytes);

    /// <summary>The notes of a source (no images): what a walk finds, read to the end.</summary>
    public static IEnumerable<ScannedFile> Scan(string root, long maxNoteBytes = IndexingOptions.DefaultMaxNoteMegabytes * 1024L * 1024L) => Find(root, maxNoteBytes, null, null, null);

    private static IEnumerable<ScannedFile> Find(string root, long maxNoteBytes, List<string>? images, List<string>? tooLarge, Action? completed)
    {
        // A walk read again starts again: the images are those of the walk that reaches its end.
        images?.Clear();
        tooLarge?.Clear();
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
                if (LengthOf(file) is not { } length)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                if (length <= maxNoteBytes)
                {
                    yield return new ScannedFile(relative, file.FullName, length, file.LastWriteTimeUtc.Ticks);
                }
                else
                {
                    tooLarge?.Add(relative);
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
