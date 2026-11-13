using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Identity;

namespace Mnemarr.Api.V1.Identity;

/// <summary>
/// How this Mnemarr presents itself (ADR 0044): the name, the logo and the version shown at the bottom of the menu. Anyone who is signed in reads it, whatever their role (the menu
/// shows it to everyone); changing it is the permission <c>settings.manage</c> of an administrator.
/// </summary>
[ApiController]
[Route("api/v1/identity")]
[Produces("application/json")]
public sealed class IdentityController : ControllerBase
{
    private readonly IdentityService _identity;

    public IdentityController(IdentityService identity)
    {
        _identity = identity;
    }

    /// <summary>The name, the colors, the logo and the version of this Mnemarr.</summary>
    [HttpGet]
    [ProducesResponseType<IdentityResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IdentityResource> Get() => MayRead ? IdentityResource.From(_identity.Get()) : Forbid();

    /// <summary>
    /// Who reads the identity: a person who is signed in (the menu shows it to them) and a key that may read the status of the system. A key that does other work (a collector's, which may be held by a
    /// machine on the Internet, or an assistant's) learns nothing of the name, the picture or the version of the environment it sends to.
    /// </summary>
    private bool MayRead => User.HasClaim(MnemarrClaims.Permission, Permissions.SystemRead) || User.FindFirst(MnemarrClaims.Kind)?.Value == nameof(AccountKind.User);

    /// <summary>
    /// The logo, served like an icon: an SVG gets a policy that forbids scripts and styles, so that opening it directly cannot run code in Mnemarr's origin. With the <c>v</c> of
    /// <c>logoUrl</c> (the hash of the file), the answer is cached for a year.
    /// </summary>
    /// <param name="v">The hash from <c>logoUrl</c>.</param>
    [HttpGet("logo")]
    [KeepsItsOwnHeaders]
    [Produces("image/png", "image/webp", "image/svg+xml")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetLogo([FromQuery] string? v)
    {
        if (!MayRead)
        {
            return Forbid();
        }

        if (_identity.Get().Logo is not { } logo)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = v == logo.Hash ? "private, max-age=31536000, immutable" : "private, max-age=300";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        Response.Headers.XContentTypeOptions = "nosniff";
        var etag = new EntityTagHeaderValue($"\"{logo.Hash}\"");
        Response.Headers.ETag = etag.ToString();

        // A browser that has the image says so: the image is not read from the database to be told it has not changed.
        if (Request.Headers.IfNoneMatch.Any(value => value == etag.ToString() || value == "*"))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return logo.Load() is { } content ? File(content, logo.ContentType, logo.Modified, etag) : NotFound();
    }

    /// <summary>Changes the name and/or the colors of the two texts. A part that is missing stays; an empty one goes back to the default.</summary>
    [HttpPut]
    [Authorize(Policy = Permissions.SettingsManage)]
    [ProducesResponseType<IdentityResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IdentityResource Set(IdentityRequest request) =>
        IdentityResource.From(_identity.Set(new IdentityChange(request.Name, request.Colors?.Name, request.Colors?.Version), User.ActorName()));

    /// <summary>Uploads the logo (a multipart form, the field <c>file</c>): a PNG, WebP or SVG of at most 1 MiB, checked by its content.</summary>
    [HttpPut("logo")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = Permissions.SettingsManage)]
    [RequestSizeLimit(IdentityRules.MaxLogoBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = IdentityRules.MaxLogoBytes + 64 * 1024)]
    [ProducesResponseType<IdentityResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IdentityResource> SetLogoAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length is 0 or > IdentityRules.MaxLogoBytes)
        {
            throw new RejectedException(Mnemarr.Core.Problem.Invalid, $"The file is required: an image of at most {IdentityRules.MaxLogoBytes / 1024} KiB.");
        }

        using var content = new MemoryStream((int)file.Length);
        await file.CopyToAsync(content, cancellationToken);
        return IdentityResource.From(_identity.SetLogo(file.FileName, content.ToArray(), User.ActorName()));
    }

    /// <summary>Stops using the uploaded logo: the one of the deployment, or Mnemarr's own, applies again.</summary>
    [HttpDelete("logo")]
    [Authorize(Policy = Permissions.SettingsManage)]
    [ProducesResponseType<IdentityResource>(StatusCodes.Status200OK)]
    public IdentityResource RemoveLogo() => IdentityResource.From(_identity.RemoveLogo(User.ActorName()));
}
