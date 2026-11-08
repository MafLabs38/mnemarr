using System.Security.Claims;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;

namespace Mnemarr.Api.V1.Security;

/// <summary>The claims of a signed-in principal.</summary>
public static class MnemarrClaims
{
    public const string Account = "mnemarr:account";
    public const string Key = "mnemarr:key";
    public const string Kind = "mnemarr:kind";
    public const string Session = "mnemarr:session";
    public const string Source = "mnemarr:source";
    public const string Scoped = "mnemarr:scoped";
    public const string Permission = "permission";

    /// <summary>Through what the caller acts, when it is not directly: <c>chat:&lt;conversation&gt;</c> for the tools that the chat's model uses with a person's token (ADR 0035).</summary>
    public const string Via = "mnemarr:via";

    /// <summary>The claims that carry what a principal may do: its role, its permissions, and the sources a key is limited to.</summary>
    public static IEnumerable<Claim> For(AccessPrincipal principal)
    {
        yield return new Claim(Account, principal.Account.Id.ToString());
        yield return new Claim(ClaimTypes.Name, principal.Account.Name);
        yield return new Claim(ClaimTypes.Role, principal.Account.Role);
        yield return new Claim(Kind, principal.Account.Kind.ToString());
        foreach (var permission in principal.Permissions)
        {
            yield return new Claim(Permission, permission);
        }

        if (principal.KeyId is { } key)
        {
            yield return new Claim(Key, key.ToString());
        }

        if (principal.Sources is { } sources)
        {
            // A key limited to sources says so, even with none left: "no source claim" alone would read as "all of them".
            yield return new Claim(Scoped, bool.TrueString);
            foreach (var source in sources)
            {
                yield return new Claim(Source, source.ToString());
            }
        }
    }

    /// <summary>The identity of a caller who is signed in: the one way it is built, for a session and for a key, whatever the scheme.</summary>
    public static ClaimsIdentity Identity(AccessPrincipal principal, string authenticationType, params Claim[] more) =>
        new([.. For(principal), .. more], authenticationType, ClaimTypes.Name, ClaimTypes.Role);

    /// <summary>
    /// What a caller may touch, rebuilt from its claims: the one way a handler gets the scope to pass to Core. A key limited to sources
    /// is limited to those (even to none); any other signed-in caller reaches all; a caller who is not signed in, none.
    /// </summary>
    public static SourceScope Scope(this ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return SourceScope.None;
        }

        // What people said to their assistants is more private than any note: it is seen by the roles that hold conversations.read, and absent for the rest.
        var hidden = !user.HasClaim(Permission, Permissions.ConversationsRead);
        return user.HasClaim(Scoped, bool.TrueString)
            ? new SourceScope([.. user.FindAll(Source).Select(claim => Guid.Parse(claim.Value))], hidden)
            : new SourceScope((IReadOnlyList<Guid>?)null, hidden);
    }

    /// <summary>The name the audit log records for whoever is acting: the person, and the chat when it is the chat's model that acts with their token.</summary>
    public static string ActorName(this ClaimsPrincipal user)
    {
        var name = user.Identity?.Name ?? AuditActors.Unknown;
        return user.FindFirstValue(Via) is { } via ? $"{name} (via {via})" : name;
    }

    /// <summary>The account behind a signed-in caller, as the claims say it (the permissions are not repeated: they are the claims'). A caller whose kind the claims do not say is taken for a service, the narrower of the two.</summary>
    public static Mnemarr.Core.Accounts.Account Person(this ClaimsPrincipal user) =>
        new(user.AccountId() ?? Guid.Empty, user.Identity?.Name ?? AuditActors.Unknown, Enum.TryParse<AccountKind>(user.FindFirstValue(Kind), out var kind) ? kind : AccountKind.Service, user.FindFirstValue(ClaimTypes.Role) ?? string.Empty, true, DateTimeOffset.UnixEpoch);

    public static Guid? AccountId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Account), out var id) ? id : null;
}
