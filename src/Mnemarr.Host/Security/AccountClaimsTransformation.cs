using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Mnemarr.Api.V1.Accounts;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Host.Security;

/// <summary>
/// Gives the principal of a cookie what its account may do, read from the database at every request (ADR 0016): the cookie only says
/// who it is and which session, so a disabled account, a changed role or a deleted account applies at once. The account was read once
/// already by the validation of the cookie, which leaves it for this request.
/// </summary>
public sealed class AccountClaimsTransformation : IClaimsTransformation
{
    /// <summary>Where the validation of the cookie leaves the account it read, for the rest of the request.</summary>
    public const string AccountItem = "mnemarr:account";

    private readonly IAccountStore _accounts;
    private readonly IHttpContextAccessor _context;

    public AccountClaimsTransformation(IAccountStore accounts, IHttpContextAccessor context)
    {
        _accounts = accounts;
        _context = context;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var session = principal.Identities.FirstOrDefault(identity => identity.AuthenticationType == AuthController.CookieScheme);
        if (session is null)
        {
            return Task.FromResult(principal);
        }

        var account = _context.HttpContext?.Items[AccountItem] as Account ?? (principal.AccountId() is { } id ? _accounts.Get(id) : null);
        if (account is not { Enabled: true, Kind: AccountKind.User })
        {
            // The cookie rejects such a session before this; a principal without claims is the safe answer if it ever gets here.
            return Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var claims = MnemarrClaims.For(new AccessPrincipal(account, account.Permissions, null, null)).Append(new Claim(MnemarrClaims.Session, session.FindFirst(MnemarrClaims.Session)?.Value ?? string.Empty));
        return Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(claims, AuthController.CookieScheme, ClaimTypes.Name, ClaimTypes.Role)));
    }
}
