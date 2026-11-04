namespace Mnemarr.Core.Indexing;

/// <summary>A Markdown file of a source: its path from the root of the source, always with <c>/</c>, and where it is.</summary>
public sealed record ScannedFile(string RelativePath, string FullPath)
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

    public static IEnumerable<ScannedFile> Scan(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        return new DirectoryInfo(root)
            .EnumerateFiles("*.md", options)
            .Where(file => file.Length <= MaxFileBytes)
            .Select(file => new ScannedFile(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.FullName));
    }
}
