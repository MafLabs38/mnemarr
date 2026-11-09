using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Mnemarr.Core;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Collectors;

/// <summary>
/// The updates of the collector that this Mnemarr carries (ADR 0032): the folder the installer made, served as it is, so that a collector that was installed asks
/// the Mnemarr it sends to for a newer version, and the installer for those who have none. Anonymous on purpose, like the programs: they are the same for
/// everybody and hold no secret (the checksum of each package is in the feed, and the collector takes an update only over HTTPS or from its own computer), and
/// only the files that the installer makes are served. Under the ingest prefix, which a proxy may publish for the collectors alone.
/// </summary>
[ApiController]
[Route("api/v1/ingest")]
[AllowAnonymous]
public sealed class UpdatesController : ControllerBase
{
    private readonly CollectorUpdates _updates;

    public UpdatesController(CollectorUpdates updates)
    {
        _updates = updates;
    }

    /// <summary>
    /// The feed: <c>releases.&lt;channel&gt;.json</c>, a few hundred bytes that say what the newest version is. It is not held back by the limit on the packages: a collector
    /// that is refused its feed because four other machines are downloading a package would not try again until its next check, hours later.
    /// </summary>
    [HttpGet("updates/{name:regex(^releases\\.[[A-Za-z0-9._-]]+\\.json$)}")]
    public IActionResult Feed(string name) => Serve(name);

    /// <summary>A package (<c>-full.nupkg</c>, <c>-delta.nupkg</c>), the installer or the portable archive: several megabytes, so limited like the programs.</summary>
    [HttpGet("updates/{name}")]
    [EnableRateLimiting(RateLimits.ProgramDownload)]
    public IActionResult File(string name) => Serve(name);

    private IActionResult Serve(string name)
    {
        var path = _updates.Open(name) ?? throw new RejectedException(Mnemarr.Core.Problem.NotFound, "This Mnemarr carries no such update.");
        // What the feed says changes with each release: it is never kept.
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, name.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "application/octet-stream", enableRangeProcessing: true);
    }

    /// <summary>The installer for a system (<c>windows-x86_64</c>), as a file: the same for every machine, with no secret in it. It asks which Mnemarr to link to when it starts.</summary>
    [HttpGet("installer/{platform}")]
    [EnableRateLimiting(RateLimits.ProgramDownload)]
    public IActionResult Installer(string platform)
    {
        var name = _updates.InstallerName(platform);
        var path = _updates.Open(name) ?? throw new RejectedException(Mnemarr.Core.Problem.NotFound, "This Mnemarr carries no installer for the collector.");
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, "application/octet-stream", name);
    }
}
