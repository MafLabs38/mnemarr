using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    private readonly AccountService _accounts;

    public AuthController(AccountService accounts)
    {
        _accounts = accounts;
    }

    /// <summary>Signs a user in with a name and a password and starts a session. The answer is the same for a wrong name and a wrong password.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<SessionResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SessionResource>> Login(LoginRequest request)
    {
        var account = _accounts.SignIn(request.Name ?? string.Empty, request.Password ?? string.Empty);
        if (account is null)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The name or the password is wrong.");
        }

        var principal = new AccessPrincipal(account, account.Permissions, null, null);
        var identity = new ClaimsIdentity([new Claim(MnemarrClaims.Account, account.Id.ToString())], CookieScheme);
        await HttpContext.SignInAsync(CookieScheme, new ClaimsPrincipal(identity));
        return Session(principal);
    }

    /// <summary>Ends the session.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ChangeOwnPassword(ChangeOwnPasswordRequest request)
    {
        var name = User.Identity?.Name ?? string.Empty;
        var id = User.AccountId() ?? Guid.Empty;
        if (_accounts.SignIn(name, request.CurrentPassword ?? string.Empty) is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The current password is wrong.");
        }

        _accounts.SetPassword(id, request.NewPassword ?? string.Empty, name);
        return NoContent();
    }

    private static SessionResource Session(AccessPrincipal principal) => new(
        principal.Account.Id,
        principal.Account.Name,
        principal.Account.Kind.ToString(),
        principal.Account.Role,
        [.. principal.Permissions.Order(StringComparer.Ordinal)]);
}
