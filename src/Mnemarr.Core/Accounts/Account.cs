namespace Mnemarr.Core.Accounts;

public enum AccountKind
{
    /// <summary>A person: a name and a password, a cookie session.</summary>
    User,

    /// <summary>An agent or an assistant: no password, keys only.</summary>
    Service,
}

public sealed record Account(Guid Id, string Name, AccountKind Kind, string Role, bool Enabled, DateTimeOffset CreatedAt)
{
    /// <summary>What the role of the account gives it, now. A role that no longer exists gives nothing.</summary>
    public IReadOnlySet<string> Permissions => Roles.Find(Role)?.Permissions ?? NoPermissions;

    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>An account with its password hash, for signing in. Never leaves the accounts code.</summary>
public sealed record AccountCredentials(Account Account, string? PasswordHash);

/// <summary>Who is acting: an account, and when it came with a key, what that key is limited to.</summary>
public sealed record AccessPrincipal(Account Account, IReadOnlySet<string> Permissions, IReadOnlyList<Guid>? Sources, Guid? KeyId)
{
    public bool Can(string permission) => Permissions.Contains(permission);

    /// <summary>Whether this principal may touch a source: a key with a list of sources is limited to them, anything else is not.</summary>
    public bool CanAccess(Guid sourceId) => Sources is null || Sources.Contains(sourceId);
}

public enum AccountProblem
{
    Invalid,
    Conflict,
    NotFound,
    Locked,
}

/// <summary>An account operation that cannot be done, with the reason to give to the person who tried.</summary>
public sealed class AccountRejectedException : Exception
{
    public AccountRejectedException(AccountProblem problem, string message)
        : base(message)
    {
        Problem = problem;
    }

    public AccountProblem Problem { get; }
}
