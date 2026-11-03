using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Mnemarr.Api.V1.Accounts;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Host.Security;

/// <summary>
/// Gives the principal of a cookie what its account may do, read from the database at every request (ADR 0016): the cookie only says
/// who it is, so a disabled account, a changed role or a deleted account applies at once. An account that is gone or disabled is no principal.
/// </summary>
public sealed class AccountClaimsTransformation : IClaimsTransformation
{
    private readonly IAccountStore _accounts;

    public AccountClaimsTransformation(IAccountStore accounts)
    {
        _accounts = accounts;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var session = principal.Identities.FirstOrDefault(identity => identity.AuthenticationType == AuthController.CookieScheme);
        if (session is null)
        {
            return Task.FromResult(principal);
        }

        var account = principal.AccountId() is { } id ? _accounts.Get(id) : null;
        if (account is not { Enabled: true, Kind: AccountKind.User })
        {
            // The cookie rejects such a session before this; a principal without claims is the safe answer if it ever gets here.
            return Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var identity = new ClaimsIdentity(
            MnemarrClaims.For(new AccessPrincipal(account, account.Permissions, null, null)), AuthController.CookieScheme, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(new ClaimsPrincipal(identity));
    }
}
