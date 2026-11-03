using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Accounts;

/// <summary>Signing in and out of the interface, and who is signed in.</summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    /// <summary>The name of the cookie scheme, known to the host that adds it.</summary>
    public const string CookieScheme = "Mnemarr.Cookie";

    /// <summary>The name of the rate-limiting policy of the sign-in call, defined by the host.</summary>
    public const string LoginLimit = "login";

    /// <summary>A name and a password are short: a body larger than this is not a sign-in.</summary>
    private const int MaxBodyBytes = 8 * 1024;

    private readonly AccountService _accounts;
    private readonly SessionService _sessions;

    public AuthController(AccountService accounts, SessionService sessions)
    {
        _accounts = accounts;
        _sessions = sessions;
    }

    /// <summary>Signs a user in with a name and a password and starts a session. The answer is the same for a wrong name and a wrong password.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginLimit)]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType<SessionResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SessionResource>> Login(LoginRequest request)
    {
        var account = _accounts.SignIn(request.Name, request.Password);
        if (account is null)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The name or the password is wrong.");
        }

        var principal = new AccessPrincipal(account, account.Permissions, null, null);
        var identity = new ClaimsIdentity(
            [new Claim(MnemarrClaims.Account, account.Id.ToString()), new Claim(MnemarrClaims.Session, _sessions.Start(account.Id))], CookieScheme);
        await HttpContext.SignInAsync(CookieScheme, new ClaimsPrincipal(identity));
        return Session(principal);
    }

    /// <summary>Ends the session.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        if (User.FindFirstValue(MnemarrClaims.Session) is { } session)
        {
            _sessions.End(session);
        }

        await HttpContext.SignOutAsync(CookieScheme);
        return NoContent();
    }

    /// <summary>Who is signed in, and what they may do: what the interface shows or hides follows it.</summary>
    [HttpGet("session")]
    [ProducesResponseType<SessionResource>(StatusCodes.Status200OK)]
    public SessionResource GetSession() => new(
        User.AccountId() ?? Guid.Empty,
        User.Identity?.Name ?? string.Empty,
        User.FindFirstValue(MnemarrClaims.Kind) ?? string.Empty,
        User.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
        [.. User.FindAll(MnemarrClaims.Permission).Select(claim => claim.Value).Order(StringComparer.Ordinal)]);

    /// <summary>A user changes their own password, with the current one.</summary>
    [HttpPut("password")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult ChangeOwnPassword(ChangeOwnPasswordRequest request)
    {
        var name = User.Identity?.Name ?? string.Empty;
        var id = User.AccountId() ?? Guid.Empty;
        if (!_accounts.VerifyPassword(id, request.CurrentPassword))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The current password is wrong.");
        }

        // The other sessions of this person end; this one goes on.
        _accounts.SetPassword(id, request.NewPassword, name, keepSession: User.FindFirstValue(MnemarrClaims.Session));
        return NoContent();
    }

    private static SessionResource Session(AccessPrincipal principal) => new(
        principal.Account.Id,
        principal.Account.Name,
        principal.Account.Kind.ToString(),
        principal.Account.Role,
        [.. principal.Permissions.Order(StringComparer.Ordinal)]);
}
