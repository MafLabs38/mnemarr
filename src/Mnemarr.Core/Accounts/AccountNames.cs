using System.Text.RegularExpressions;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Accounts;

/// <summary>What a name can be. Counters are keyed by names, and the audit log names its actors, so only what is a name is ever counted or recorded.</summary>
public static partial class AccountNames
{
    public const int MaxLength = 64;

    /// <summary>Names the audit log gives to actors that are not accounts: no account may take one, or its events could pass for the system's.</summary>
    private static readonly string[] Reserved = [AuditActors.System, AuditActors.Unknown];

    public static bool IsValid(string name) =>
        Pattern().IsMatch(name) && !Reserved.Contains(name, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{1,63}\z")]
    private static partial Regex Pattern();
}
