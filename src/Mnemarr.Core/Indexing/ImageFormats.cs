namespace Mnemarr.Core.Indexing;

/// <summary>
/// The image formats a note may show, decided by the extension of the file and never by what is in it. SVG is not one of them: it can
/// carry a script.
/// </summary>
public static class ImageFormats
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
    };

    /// <summary>The type to answer with for a file of this name, or null when it is not an image a note may show.</summary>
    public static string? ContentType(string path) => Types.GetValueOrDefault(Path.GetExtension(path));

    public static bool Is(string path) => ContentType(path) is not null;
}
