using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Accounts;

/// <summary>The accounts and the roles: what an administrator manages.</summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = Permissions.AccountsManage)]
public sealed class AccountsController : ControllerBase
{
    private readonly IAccountStore _store;
    private readonly AccountService _accounts;

    public AccountsController(IAccountStore store, AccountService accounts)
    {
        _store = store;
        _accounts = accounts;
    }

    private string Actor => User.Identity?.Name ?? "unknown";

    [HttpGet("roles")]
    public IReadOnlyList<RoleResource> GetRoles() => [.. Roles.All.Select(RoleResource.From)];

    [HttpGet("accounts")]
    public IReadOnlyList<AccountResource> GetAccounts() => [.. _store.List().Select(AccountResource.From)];

    [HttpGet("accounts/{id:guid}")]
    [ProducesResponseType<AccountResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AccountResource> GetAccount(Guid id) =>
        _store.Get(id) is { } account ? AccountResource.From(account) : NotFound();

    [HttpPost("accounts")]
    [ProducesResponseType<AccountResource>(StatusCodes.Status201Created)]
    public ActionResult<AccountResource> CreateAccount(CreateAccountRequest request)
    {
        if (!Enum.TryParse<AccountKind>(request.Kind, ignoreCase: false, out var kind))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The kind of an account is User or Service.");
        }

        var account = _accounts.Create(request.Name ?? string.Empty, kind, request.Role ?? string.Empty, request.Password, Actor);
        return CreatedAtAction(nameof(GetAccount), new { id = account.Id }, AccountResource.From(account));
    }

    [HttpPut("accounts/{id:guid}")]
    public ActionResult<AccountResource> UpdateAccount(Guid id, UpdateAccountRequest request)
    {
        var current = _store.Get(id);
        if (current is null)
        {
            return NotFound();
        }

        var account = request.Role == current.Role ? current : _accounts.ChangeRole(id, request.Role ?? string.Empty, Actor);
        return AccountResource.From(request.Enabled == account.Enabled ? account : _accounts.SetEnabled(id, request.Enabled, Actor));
    }

    [HttpPut("accounts/{id:guid}/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult SetPassword(Guid id, SetPasswordRequest request)
    {
        _accounts.SetPassword(id, request.Password ?? string.Empty, Actor);
        return NoContent();
    }

    [HttpDelete("accounts/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteAccount(Guid id)
    {
        _accounts.Delete(id, Actor);
        return NoContent();
    }
}
