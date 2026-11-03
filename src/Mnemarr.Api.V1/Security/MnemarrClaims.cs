using System.Security.Claims;
using Mnemarr.Core.Accounts;

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

    /// <summary>
    /// The one way to ask whether a caller may touch a source. A key limited to sources may touch those; any other signed-in caller, all of
    /// them; a caller who is not signed in, none. Fails closed: a scope that cannot be read is a scope of nothing.
    /// </summary>
    public static bool CanAccessSource(this ClaimsPrincipal user, Guid sourceId) =>
        user.Identity?.IsAuthenticated == true && (!user.HasClaim(Scoped, bool.TrueString) || user.HasClaim(Source, sourceId.ToString()));

    public static Guid? AccountId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Account), out var id) ? id : null;
}
