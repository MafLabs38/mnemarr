using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Accounts;

/// <summary>The API keys of an account.</summary>
[ApiController]
[Route("api/v1/accounts")]
[Authorize(Policy = Permissions.AccountsManage)]
public sealed class ApiKeysController : ControllerBase
{
    private readonly IAccountStore _store;
    private readonly ApiKeyService _keys;

    public ApiKeysController(IAccountStore store, ApiKeyService keys)
    {
        _store = store;
        _keys = keys;
    }

    private string Actor => User.ActorName();

    [HttpGet("{accountId:guid}/keys")]
    public ActionResult<IReadOnlyList<ApiKeyResource>> GetKeys(Guid accountId) =>
        _store.Get(accountId) is null ? NotFound() : Ok(_keys.List(accountId).Select(ApiKeyResource.From).ToList());

    /// <summary>Creates a key. Its secret is in this answer and nowhere else, ever.</summary>
    [HttpPost("{accountId:guid}/keys")]
    [ProducesResponseType<CreatedApiKeyResource>(StatusCodes.Status201Created)]
    public ActionResult<CreatedApiKeyResource> CreateKey(Guid accountId, CreateApiKeyRequest request)
    {
        var created = _keys.Create(accountId, request.Name, request.Sources, request.ExpiresAt, Actor);
        return StatusCode(StatusCodes.Status201Created, new CreatedApiKeyResource(ApiKeyResource.From(created.Key), created.Secret));
    }

    [HttpDelete("{accountId:guid}/keys/{keyId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult RevokeKey(Guid accountId, Guid keyId)
    {
        _keys.Revoke(accountId, keyId, Actor);
        return NoContent();
    }
}
