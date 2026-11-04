namespace Mnemarr.Core.Indexing;

/// <summary>Where a link written in a note leads, from the folder of the note, inside its source: one reading of paths for the index and for what is shown.</summary>
internal static class NotePaths
{
    /// <summary>
    /// The target of a link written from the folder of a note: unchanged when it is a name or a path from the root, resolved (and lower case, as
    /// targets are stored) when it starts with <c>./</c> or <c>../</c>, null when it leaves the source.
    /// </summary>
    public static string? FromFolderTarget(string notePath, string target) =>
        target.StartsWith("./", StringComparison.Ordinal) || target.StartsWith("../", StringComparison.Ordinal)
            ? Walk(notePath, target)?.ToLowerInvariant()
            : target;

    /// <summary>
    /// The path, from the root of the source, of what a relative address leads to from the note at <paramref name="notePath"/> (no heading or
    /// query, URL-decoded), or null when it leaves the source or is not a relative address (a scheme, a leading <c>/</c>, <c>#</c> or <c>?</c>).
    /// </summary>
    public static string? RelativePath(string notePath, string address)
    {
        var colon = address.IndexOf(':', StringComparison.Ordinal);
        var slash = address.IndexOf('/', StringComparison.Ordinal);
        if (address.Length == 0 || address[0] is '/' or '#' or '?' || (colon > 0 && (slash < 0 || colon < slash)))
        {
            return null;
        }

        var end = address.IndexOfAny(['#', '?']);
        return Walk(notePath, Uri.UnescapeDataString(end >= 0 ? address[..end] : address).Replace('\\', '/'));
    }

    /// <summary>The path a reference leads to from the folder of the note, <c>..</c> and <c>.</c> applied; null when it climbs out of the source.</summary>
    private static string? Walk(string notePath, string reference)
    {
        var parts = notePath.Split('/').SkipLast(1).ToList();
        foreach (var part in reference.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }
}
