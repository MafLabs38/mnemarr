using System.Text.RegularExpressions;
using System.Xml;

namespace Mnemarr.Core.Identity;

/// <summary>What a logo file is, once inspected: the extension it must carry and the content type it is served with.</summary>
public sealed record LogoFormat(string Extension, string ContentType);

/// <summary>
/// The rules of the identity that the sidebar shows (ADR 0044): the name, the colors of the texts and the logo. One place, shared by the settings, the upload and the service
/// that serves the file. The same rules as Portarr's identity badge, so that the two products say the same thing about what they accept.
/// </summary>
public static partial class IdentityRules
{
    /// <summary>A logo is small: the same limit as an icon.</summary>
    public const long MaxLogoBytes = 1024 * 1024;

    public const int MaxNameLength = 40;

    /// <summary>The name of the product, when nothing else is said.</summary>
    public const string DefaultName = "Mnemarr";

    /// <summary>Mnemarr's accent (the yellow of the active entry of the menu) for both texts.</summary>
    public const string DefaultColor = "accent";

    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    private static readonly LogoFormat Png = new("png", "image/png");
    private static readonly LogoFormat WebP = new("webp", "image/webp");
    private static readonly LogoFormat Svg = new("svg", "image/svg+xml");
    private static readonly LogoFormat[] Formats = [Png, WebP, Svg];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The theme tokens a color may name: the same list as the interface's.</summary>
    public static IReadOnlyList<string> ColorTokens { get; } = ["accent", "accentDeep", "secondary", "ink", "inkSoft", "inkMuted"];

    /// <summary>The formats a logo may have, by extension.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [.. Formats.Select(format => format.Extension)];

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexPattern();

    /// <summary>The format an extension (with or without its dot, any case) stands for, or null.</summary>
    public static LogoFormat? FormatOfExtension(string extension) =>
        Formats.FirstOrDefault(format => string.Equals(format.Extension, extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    /// <summary>The name: one line of text of at most <see cref="MaxNameLength"/> characters. Null when valid.</summary>
    public static string? ValidateName(string name)
    {
        if (name.Length > MaxNameLength)
        {
            return $"The name must be at most {MaxNameLength} characters.";
        }

        return name.Any(char.IsControl) ? "The name must be one line of text." : null;
    }

    /// <summary>Whether the value is a hex color (<c>#rgb</c>, <c>#rrggbb</c>) or one of <see cref="ColorTokens"/>: nothing else, so that a value never carries CSS beyond one color.</summary>
    public static bool IsColor(string value) => HexPattern().IsMatch(value) || ColorTokens.Contains(value, StringComparer.Ordinal);

    /// <summary>The reason a color is refused, or null.</summary>
    public static string? ValidateColor(string field, string value) =>
        IsColor(value) ? null : $"The {field} color must be a hex color (#rgb or #rrggbb) or one of: {string.Join(", ", ColorTokens)}.";

    /// <summary>Checks the bytes of a logo against its extension: the signature of a png or a webp, or a well-formed svg without script, event handler or foreign content.</summary>
    public static (LogoFormat? Format, string? Error) InspectLogo(ReadOnlySpan<byte> content, string extension)
    {
        var expected = FormatOfExtension(extension);
        if (expected is null)
        {
            return (null, $"The file must be a {string.Join(", ", Extensions)} image.");
        }

        if (content.Length == 0 || content.Length > MaxLogoBytes)
        {
            return (null, $"The image must be between 1 byte and {MaxLogoBytes / 1024} KiB.");
        }

        return HasValidContent(expected, content) ? (expected, null) : (null, $"The file is not a valid {expected.Extension} image.");
    }

    private static bool HasValidContent(LogoFormat expected, ReadOnlySpan<byte> content)
    {
        if (expected == Png)
        {
            return content.StartsWith(PngSignature);
        }

        if (expected == WebP)
        {
            return content.Length > 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8);
        }

        return IsSafeSvg(content);
    }

    private static bool IsSafeSvg(ReadOnlySpan<byte> content)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxLogoBytes };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(content.ToArray()), settings);
            var root = true;
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                if ((root && (reader.LocalName != "svg" || reader.NamespaceURI != SvgNamespace)) || IsActive(reader))
                {
                    return false;
                }

                root = false;

                // A style sheet is allowed, but one that loads anything from outside is not. Reading its text moves past it.
                if (reader.LocalName != "style")
                {
                    reader.Read();
                }
                else if (LoadsFromOutside(reader.ReadElementContentAsString()))
                {
                    return false;
                }
            }

            return !root;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>CSS that imports a sheet or points <c>url()</c> anywhere but at a fragment of the same document.</summary>
    private static bool LoadsFromOutside(string css)
    {
        if (css.Contains("@import", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var start = css.IndexOf("url(", StringComparison.OrdinalIgnoreCase); start >= 0; start = css.IndexOf("url(", start + 4, StringComparison.OrdinalIgnoreCase))
        {
            if (!css[(start + 4)..].TrimStart(' ', '"', '\'').StartsWith('#'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An element that can run code or load content: a script, a foreign object, an event handler, a script link, a link to another document, a style sheet that loads from outside.</summary>
    private static bool IsActive(XmlReader element)
    {
        if (element.LocalName is "script" or "foreignObject")
        {
            return true;
        }

        for (var more = element.MoveToFirstAttribute(); more; more = element.MoveToNextAttribute())
        {
            var value = element.Value.TrimStart();
            if (element.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)
                || (element.LocalName == "href" && value.Length > 0 && !value.StartsWith('#'))
                || (element.LocalName == "style" && LoadsFromOutside(value)))
            {
                return true;
            }
        }

        element.MoveToElement();
        return false;
    }
}
