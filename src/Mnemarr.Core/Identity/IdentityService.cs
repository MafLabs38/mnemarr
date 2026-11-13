using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Identity;

/// <summary>An image that is the logo: its bytes, how it is served, the hash that goes in its address, and when it was set.</summary>
public sealed record IdentityLogoFile(byte[] Content, string ContentType, string Hash, DateTimeOffset Modified);

/// <summary>The colors of the two texts, resolved: each is a theme token name or a hex color.</summary>
public sealed record IdentityColors(string Name, string Version);

/// <summary>How this Mnemarr presents itself, resolved (ADR 0044). <c>LogoProblem</c> says why a logo that was given is not used.</summary>
public sealed record ResolvedIdentity(string Name, IdentityColors Colors, IdentityLogoFile? Logo, string? LogoProblem);

/// <summary>What to change: a part that is null stays as it is, an empty one goes back to the default, any other is checked and kept.</summary>
public sealed record IdentityChange(string? Name = null, string? NameColor = null, string? VersionColor = null);

/// <summary>
/// The identity of this Mnemarr, shown at the bottom of the menu: a name, an image and the version (ADR 0044). What an administrator set in the interface wins, then what the deployment
/// said (<c>Identity</c> settings), then the product's own (its name, its logo, the accent). Everything that is given is checked before it is kept, and every change is audited.
/// </summary>
public sealed class IdentityService
{
    private readonly IIdentityStore _store;
    private readonly IAuditLog _audit;
    private readonly BrandingOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _fileLock = new();
    private (string Path, DateTime Modified, long Length, IdentityLogoFile? Logo, string? Problem)? _file;

    public IdentityService(IIdentityStore store, IAuditLog audit, IOptions<BrandingOptions> options, TimeProvider clock)
    {
        _store = store;
        _audit = audit;
        _options = options.Value;
        _clock = clock;
    }

    public ResolvedIdentity Get()
    {
        var stored = _store.Get();
        var colors = new IdentityColors(
            Pick(stored.NameColor, _options.Colors.Name), Pick(stored.VersionColor, _options.Colors.Version));
        var name = Pick(stored.Name, _options.Name, IdentityRules.DefaultName);
        if (stored.Logo is { } content && stored.LogoType is { } type && stored.LogoHash is { } hash)
        {
            return new ResolvedIdentity(name, colors, new IdentityLogoFile(content, type, hash, stored.LogoAt ?? DateTimeOffset.UnixEpoch), null);
        }

        var (logo, problem) = DeploymentLogo();
        return new ResolvedIdentity(name, colors, logo, problem);
    }

    /// <summary>Changes the name and the colors. A value that is not valid refuses the whole change.</summary>
    public ResolvedIdentity Set(IdentityChange change, string actor)
    {
        var stored = _store.Get();
        var name = Merge(stored.Name, change.Name, IdentityRules.ValidateName);
        var nameColor = Merge(stored.NameColor, change.NameColor, value => IdentityRules.ValidateColor("name", value));
        var versionColor = Merge(stored.VersionColor, change.VersionColor, value => IdentityRules.ValidateColor("version", value));
        _store.Save(stored with { Name = name, NameColor = nameColor, VersionColor = versionColor });
        _audit.Append(actor, "identity.updated", "identity");
        return Get();
    }

    /// <summary>Keeps an image as the logo, once its content has been checked against its extension.</summary>
    public ResolvedIdentity SetLogo(string fileName, byte[] content, string actor)
    {
        var (format, error) = IdentityRules.InspectLogo(content, Path.GetExtension(fileName));
        if (format is null)
        {
            throw new RejectedException(Problem.Invalid, error ?? "The file is not a valid image.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        _store.Save(_store.Get() with { Logo = content, LogoType = format.ContentType, LogoHash = hash, LogoAt = _clock.GetUtcNow() });
        _audit.Append(actor, "identity.logo.updated", "identity", format.Extension);
        return Get();
    }

    /// <summary>Stops using the uploaded logo: the one of the deployment, or the product's own, applies again.</summary>
    public ResolvedIdentity RemoveLogo(string actor)
    {
        _store.Save(_store.Get() with { Logo = null, LogoType = null, LogoHash = null, LogoAt = null });
        _audit.Append(actor, "identity.logo.removed", "identity");
        return Get();
    }

    private static string Pick(string? stored, string? deployed, string fallback = IdentityRules.DefaultColor)
    {
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        return string.IsNullOrWhiteSpace(deployed) ? fallback : deployed.Trim();
    }

    /// <summary>The stored value after a change: unchanged for null, forgotten for an empty one, the checked value otherwise.</summary>
    private static string? Merge(string? current, string? given, Func<string, string?> check)
    {
        if (given is null)
        {
            return current;
        }

        var value = given.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return check(value) is { } problem ? throw new RejectedException(Problem.Invalid, problem) : value;
    }

    /// <summary>The image that the deployment points to, read again only when the file changed.</summary>
    private (IdentityLogoFile? Logo, string? Problem) DeploymentLogo()
    {
        if (string.IsNullOrWhiteSpace(_options.LogoFile))
        {
            return (null, null);
        }

        lock (_fileLock)
        {
            var info = new FileInfo(_options.LogoFile);
            if (_file is { } known && known.Path == info.FullName && info.Exists && known.Modified == info.LastWriteTimeUtc && known.Length == info.Length)
            {
                return (known.Logo, known.Problem);
            }

            var (logo, problem) = ReadDeploymentLogo(info);
            _file = (info.FullName, info.Exists ? info.LastWriteTimeUtc : default, info.Exists ? info.Length : 0, logo, problem);
            return (logo, problem);
        }
    }

    private static (IdentityLogoFile? Logo, string? Problem) ReadDeploymentLogo(FileInfo info)
    {
        try
        {
            if (!info.Exists)
            {
                return (null, "The logo file of the settings is not there.");
            }

            if (info.Length > IdentityRules.MaxLogoBytes)
            {
                return (null, $"The logo file of the settings is larger than {IdentityRules.MaxLogoBytes / 1024} KiB.");
            }

            var content = File.ReadAllBytes(info.FullName);
            var (format, error) = IdentityRules.InspectLogo(content, info.Extension);
            return format is null
                ? (null, $"The logo file of the settings is not used: {error}")
                : (new IdentityLogoFile(content, format.ContentType, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), info.LastWriteTimeUtc), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "The logo file of the settings cannot be read.");
        }
    }
}
