using System.Globalization;

namespace Mnemarr.Core.Storage;

/// <summary>How a moment is written in the database: ISO 8601, round-trippable, the same everywhere.</summary>
public static class StorageText
{
    public static string Format(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
}
