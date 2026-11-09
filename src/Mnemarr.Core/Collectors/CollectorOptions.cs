using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Collectors;

/// <summary>Settings of the machines, from the <c>Collectors</c> section (ADR 0027 §7).</summary>
public sealed class CollectorOptions
{
    public const int MaxMinutes = 7 * 24 * 60;

    /// <summary>A machine heard from within this many minutes is online.</summary>
    public int OnlineWithinMinutes { get; set; } = 5;

    /// <summary>An always-on machine not heard from for this many minutes is an alert.</summary>
    public int SilenceAfterMinutes { get; set; } = 30;

    /// <summary>
    /// A folder that holds the programs a machine installs (one file per system, named as in <see cref="CollectorDownloads"/>): Mnemarr serves them,
    /// so that a machine gets the program of the version of its server from the server it trusts. The image carries them; a developer mounts a folder.
    /// </summary>
    public string? DownloadFolder { get; set; }

    /// <summary>
    /// The folder of the updates of the collector that the installer made (the feed, the packages, the installer; ADR 0032). Without it, the folder <c>updates</c> of
    /// <see cref="DownloadFolder"/>. The image carries it.
    /// </summary>
    public string? UpdateFolder { get; set; }

    /// <summary>Whether this Mnemarr serves the installer and the updates of the collector it carries (on by default). Off: it serves none, and a collector never takes an update from it.</summary>
    public bool ServeUpdates { get; set; } = true;

    /// <summary>The address machines reach Mnemarr at, when the one a request came with is not it (behind a proxy that does not say so). Without it, the address of the request is used.</summary>
    public string? PublicUrl { get; set; }

    public TimeSpan OnlineWithin => TimeSpan.FromMinutes(OnlineWithinMinutes);

    public TimeSpan SilenceAfter => TimeSpan.FromMinutes(SilenceAfterMinutes);
}

public sealed class CollectorOptionsValidator : IValidateOptions<CollectorOptions>
{
    public ValidateOptionsResult Validate(string? name, CollectorOptions options)
    {
        if (options.OnlineWithinMinutes is < 1 or > CollectorOptions.MaxMinutes)
        {
            return ValidateOptionsResult.Fail($"Collectors:OnlineWithinMinutes (MNEMARR__COLLECTORS__ONLINEWITHINMINUTES) must be between 1 and {CollectorOptions.MaxMinutes}.");
        }

        if (options.SilenceAfterMinutes < options.OnlineWithinMinutes || options.SilenceAfterMinutes > CollectorOptions.MaxMinutes)
        {
            return ValidateOptionsResult.Fail($"Collectors:SilenceAfterMinutes (MNEMARR__COLLECTORS__SILENCEAFTERMINUTES) must be at least OnlineWithinMinutes and at most {CollectorOptions.MaxMinutes}.");
        }

        return string.IsNullOrWhiteSpace(options.PublicUrl) || (Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.Query))
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Collectors:PublicUrl (MNEMARR__COLLECTORS__PUBLICURL) must be an http or https address, such as https://mnemarr.homelab.example.");
    }
}

/// <summary>Settings of the ingest, from the <c>Ingest</c> section.</summary>
public sealed class IngestOptions
{
    /// <summary>
    /// Whether a key may send only for the machine it was given to. On by default; off for a sandbox, or the day before every machine is enrolled,
    /// when a hand-made <c>Collector</c> account is still allowed to write under any machine name.
    /// </summary>
    public bool RequireEnrolment { get; set; } = true;
}
