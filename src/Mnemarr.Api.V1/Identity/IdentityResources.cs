using Mnemarr.Core.Identity;
using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Api.V1.Identity;

/// <summary>The colors of the two texts of the badge: a hex color or a theme token.</summary>
public sealed record IdentityColorsResource(string Name, string Version);

/// <summary>
/// How this Mnemarr presents itself at the bottom of the menu (ADR 0044): its name, the address of its logo (with the hash of the file in it, so that the image is cached for as long as
/// it does not change; null for the product's own), the version that runs, and why a logo that was given is not used.
/// </summary>
public sealed record IdentityResource(string Name, string? LogoUrl, string Version, string? LogoProblem, IdentityColorsResource Colors)
{
    public static IdentityResource From(ResolvedIdentity identity) =>
        new(
            identity.Name,
            identity.Logo is { } logo ? $"/api/v1/identity/logo?v={logo.Hash}" : null,
            BuildInfo.Version,
            identity.LogoProblem,
            new IdentityColorsResource(identity.Colors.Name, identity.Colors.Version));
}

/// <summary>What to change: a part that is missing stays as it is, an empty one goes back to the default.</summary>
public sealed record IdentityColorsRequest(string? Name, string? Version);

public sealed record IdentityRequest(string? Name, IdentityColorsRequest? Colors);
