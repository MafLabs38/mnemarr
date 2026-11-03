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
    /// <summary>A name and a password are short: a body larger than this is not a sign-in.</summary>
    private const int MaxBodyBytes = 8 * 1024;

    private readonly SignInService _signIn;
    private readonly AccountService _accounts;

    public AuthController(SignInService signIn, AccountService accounts)
    {
        _signIn = signIn;
        _accounts = accounts;
    }

    /// <summary>Signs a user in with a name and a password and starts a session. The answer is the same for a wrong name and a wrong password.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType<SessionResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SessionResource>> Login(LoginRequest request)
    {
        if (await _signIn.SignInAsync(request.Name, request.Password, HttpContext.RequestAborted) is not var (account, session))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The name or the password is wrong.");
        }

        var principal = new AccessPrincipal(account, account.Permissions, null, null);
        var identity = new ClaimsIdentity([new Claim(MnemarrClaims.Account, account.Id.ToString()), new Claim(MnemarrClaims.Session, session)], MnemarrSchemes.Cookie);
        await HttpContext.SignInAsync(MnemarrSchemes.Cookie, new ClaimsPrincipal(identity));
        return SessionResource.From(new ClaimsPrincipal(new ClaimsIdentity(MnemarrClaims.For(principal), MnemarrSchemes.Cookie, ClaimTypes.Name, ClaimTypes.Role)));
    }

    /// <summary>Ends the session.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        if (User.FindFirstValue(MnemarrClaims.Session) is { } session)
        {
            _signIn.EndSession(session);
        }

        await HttpContext.SignOutAsync(MnemarrSchemes.Cookie);
        return NoContent();
    }

    /// <summary>Who is signed in, and what they may do: what the interface shows or hides follows it.</summary>
    [HttpGet("session")]
    [ProducesResponseType<SessionResource>(StatusCodes.Status200OK)]
    public SessionResource GetSession() => SessionResource.From(User);

    /// <summary>A user changes their own password, with the current one.</summary>
    [HttpPut("password")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ChangeOwnPassword(ChangeOwnPasswordRequest request)
    {
        var name = User.Identity?.Name ?? string.Empty;
        var id = User.AccountId() ?? Guid.Empty;
        if (!await _signIn.VerifyPasswordAsync(id, request.CurrentPassword, HttpContext.RequestAborted))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The current password is wrong.");
        }

        // The other sessions of this person end; this one goes on.
        _accounts.SetPassword(id, request.NewPassword, name, keepSession: User.FindFirstValue(MnemarrClaims.Session));
        return NoContent();
    }
}
