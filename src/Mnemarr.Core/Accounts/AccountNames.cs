using System.Text.RegularExpressions;

namespace Mnemarr.Core.Accounts;

/// <summary>What a name can be. Counters are keyed by names, so only what is a name is ever counted.</summary>
public static partial class AccountNames
{
    public const int MaxLength = 64;

    public static bool IsValid(string name) => Pattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$")]
    private static partial Regex Pattern();
}
