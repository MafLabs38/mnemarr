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
    /// <summary>A user that is not stored, for the hasher to bind a hash to a name: a new account's password, and the decoy.</summary>
    public static Account Placeholder(string name) => new(Guid.Empty, name, AccountKind.User, Roles.Reader, false, DateTimeOffset.UnixEpoch);

    /// <summary>What the role of the account gives it, now. A role that no longer exists gives nothing.</summary>
    public IReadOnlySet<string> Permissions => Roles.Find(Role)?.Permissions ?? NoPermissions;

    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>A user who has signed in, and the id of the session that was started for them.</summary>
public sealed record SignedIn(Account Account, string SessionId);

/// <summary>An account with its password hash, for signing in. Never leaves the accounts code.</summary>
public sealed record AccountCredentials(Account Account, string? PasswordHash);

/// <summary>
/// The sources a caller may touch: all of them, or the list a key is limited to (an empty list is none). This is the only place the rule
/// lives; the queries of the verticals that serve sources, notes and the MCP server take one as a parameter, so that none can be written
/// without the question being asked.
/// </summary>
public sealed record SourceScope(IReadOnlyList<Guid>? Sources, bool ConversationsHidden = true)
{
    /// <summary>Everything: what Mnemarr itself uses to do its own work. A caller gets a scope of its own (see <see cref="AccessPrincipal.Scope"/>).</summary>
    public static SourceScope All { get; } = new((IReadOnlyList<Guid>?)null, ConversationsHidden: false);

    /// <summary>What a caller that is not signed in may touch. A scope hides the conversations unless it is said not to: forgetting is the safe mistake.</summary>
    public static SourceScope None { get; } = new([]);

    public bool CanAccess(Guid sourceId) => Sources is null || Sources.Contains(sourceId);

    /// <summary>
    /// Whether the caller may see a source: it is among those the caller may touch, and it is not the conversations of the assistants when the
    /// caller does not hold <see cref="Permissions.ConversationsRead"/>. A source that may not be seen is as absent as one that does not exist.
    /// </summary>
    public bool CanSee(Mnemarr.Core.Sources.Source source) => CanAccess(source.Id) && !(ConversationsHidden && source.Kind == Mnemarr.Core.Sources.SourceKind.Conversations);
}

/// <summary>Who is acting: an account, and when it came with a key, what that key is limited to.</summary>
public sealed record AccessPrincipal(Account Account, IReadOnlySet<string> Permissions, IReadOnlyList<Guid>? Sources, Guid? KeyId)
{
    public SourceScope Scope => new(Sources, ConversationsHidden: !Permissions.Contains(Mnemarr.Core.Accounts.Permissions.ConversationsRead));

    public bool Can(string permission) => Permissions.Contains(permission);

    public bool CanAccess(Guid sourceId) => Scope.CanAccess(sourceId);
}
