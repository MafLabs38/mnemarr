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

/// <summary>
/// The sources a caller may touch: all of them, or the list a key is limited to (an empty list is none). This is the only place the rule
/// lives; the queries of the verticals that serve sources, notes and the MCP server take one as a parameter, so that none can be written
/// without the question being asked.
/// </summary>
public sealed record SourceScope(IReadOnlyList<Guid>? Sources)
{
    public static SourceScope All { get; } = new((IReadOnlyList<Guid>?)null);

    /// <summary>What a caller that is not signed in may touch.</summary>
    public static SourceScope None { get; } = new([]);

    public bool CanAccess(Guid sourceId) => Sources is null || Sources.Contains(sourceId);
}

/// <summary>Who is acting: an account, and when it came with a key, what that key is limited to.</summary>
public sealed record AccessPrincipal(Account Account, IReadOnlySet<string> Permissions, IReadOnlyList<Guid>? Sources, Guid? KeyId)
{
    public SourceScope Scope => new(Sources);

    public bool Can(string permission) => Permissions.Contains(permission);

    public bool CanAccess(Guid sourceId) => Scope.CanAccess(sourceId);
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
