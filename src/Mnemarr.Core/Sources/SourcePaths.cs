using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Core.Sources;

/// <summary>
/// Where a source may be: a folder the operator put at Mnemarr's disposal (the roots of <see cref="SourcesOptions"/>, by default the
/// <c>sources</c> folder of the data folder), reached without crossing a link. The check is made when a source is declared and again before
/// every scan, because the roots can be narrowed and a folder can be swapped for a link (by a pull) after the declaration.
/// </summary>
public sealed class SourcePaths
{
    private readonly IOptions<MnemarrOptions> _options;
    private readonly IOptions<SourcesOptions> _sources;

    public SourcePaths(IOptions<MnemarrOptions> options, IOptions<SourcesOptions> sources)
    {
        _options = options;
        _sources = sources;
    }

    /// <summary>The folders a source may be in.</summary>
    public IReadOnlyList<string> Roots() =>
        _sources.Value.Roots.Length > 0
            ? [.. _sources.Value.Roots.Select(Path.GetFullPath)]
            : [Path.GetFullPath(Path.Combine(_options.Value.DataFolder, "sources"))];

    /// <summary>
    /// The full path of a folder a source may be in, or a refusal. Absolute, inside a root, and no link between the root and the folder:
    /// a link below the root can lead anywhere the process can read, and a repository can contain one. The root itself may be a link, as
    /// it is the operator's choice.
    /// </summary>
    public string Allowed(string path)
    {
        var full = Normalize(path);
        var root = Roots().Select(Path.TrimEndingDirectorySeparator).FirstOrDefault(candidate => IsInside(candidate, full));
        if (root is null)
        {
            throw new RejectedException(Problem.Invalid, "The path of a source must be inside one of the folders the operator allows for sources.");
        }

        var current = root;
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Where(part => part != "."))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).LinkTarget is not null)
            {
                throw new RejectedException(Problem.Invalid, "The folder of a source cannot be, or be below, a link.");
            }
        }

        return full;
    }

    /// <summary>The full form of an absolute path, without any look at the disk; a path that is not absolute or holds a character no path can hold is refused as invalid, not allowed to fail later.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
        {
            throw new RejectedException(Problem.Invalid, "The path of a source must be an absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>The file or any folder between it and the source's folder is a link. The check and the read are two steps, so a swap in between is still possible: the window is narrowed, not closed (SECURITY.md).</summary>
    internal static bool CrossesLink(string file, string root)
    {
        try
        {
            if (new FileInfo(file).LinkTarget is not null)
            {
                return true;
            }

            for (var folder = Path.GetDirectoryName(file); folder is not null && folder.Length > root.Length; folder = Path.GetDirectoryName(folder))
            {
                if (new DirectoryInfo(folder).LinkTarget is not null)
                {
                    return true;
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
