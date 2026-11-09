namespace Mnemarr.Core.Collectors;

/// <summary>
/// The versions of the collector programs, as the installer names them: semantic versions (<c>1.2.3</c>, <c>0.0.0-dev.12</c>). Compared by their precedence:
/// the numbers first, a release after its pre-releases, the parts of a pre-release one by one (numbers as numbers).
/// </summary>
public static class ProgramVersions
{
    /// <summary>Whether the text is a semantic version, and its parts.</summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100)
        {
            return false;
        }

        var core = text.Trim();
        var plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            core = core[..plus];
        }

        var dash = core.IndexOf('-', StringComparison.Ordinal);
        var pre = dash >= 0 ? core[(dash + 1)..] : string.Empty;
        var numbers = (dash >= 0 ? core[..dash] : core).Split('.');
        if (numbers.Length != 3 || (dash >= 0 && pre.Length == 0))
        {
            return false;
        }

        if (!int.TryParse(numbers[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(numbers[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(numbers[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var parts = pre.Length == 0 ? [] : pre.Split('.');
        if (parts.Any(string.IsNullOrEmpty) || numbers.Any(number => number.Length > 1 && number[0] == '0') || parts.Any(part => part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, parts);
        return true;
    }

    /// <summary>Which of two versions comes first: negative when <paramref name="a"/> is older, positive when it is newer.</summary>
    public static int Compare(SemanticVersion a, SemanticVersion b)
    {
        var numbers = (a.Major, a.Minor, a.Patch).CompareTo((b.Major, b.Minor, b.Patch));
        if (numbers != 0)
        {
            return numbers;
        }

        // A release is newer than its pre-releases.
        if (a.Pre.Length == 0 || b.Pre.Length == 0)
        {
            return b.Pre.Length.CompareTo(a.Pre.Length);
        }

        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var part = ComparePart(a.Pre[i], b.Pre[i]);
            if (part != 0)
            {
                return part;
            }
        }

        return a.Pre.Length.CompareTo(b.Pre.Length);
    }

    private static int ComparePart(string a, string b)
    {
        var aNumber = long.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var x);
        var bNumber = long.TryParse(b, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var y);
        return (aNumber, bNumber) switch
        {
            (true, true) => x.CompareTo(y),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(a, b),
        };
    }
}

/// <summary>The parts of a semantic version.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string[] Pre);
