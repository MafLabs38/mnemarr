using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Core;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Collectors;

/// <summary>
/// What a machine fetches to join (ADR 0027 §12): the installer, and the program it installs. Anonymous on purpose, under the ingest prefix that a
/// proxy may publish for the collectors alone: the program is the same for everybody, and holds no secret; what makes a machine join is the code,
/// which is in the line a person runs (or in the program they download), and is of no use unless a person approved it. Only the files that are named in the code are served.
/// </summary>
[ApiController]
[Route("api/v1/ingest")]
[AllowAnonymous]
public sealed class InstallController : ControllerBase
{
    private const string PlainText = "text/plain; charset=utf-8";
    private readonly CollectorDownloads _downloads;
    private readonly InstallCommands _commands;
    private readonly EnrolmentService _enrolments;

    public InstallController(CollectorDownloads downloads, InstallCommands commands, EnrolmentService enrolments)
    {
        _downloads = downloads;
        _commands = commands;
        _enrolments = enrolments;
    }

    /// <summary>
    /// The program for one system, as a file. With a code and a machine it is the program that carries them, named after the machine: opened, it
    /// joins that machine and installs itself. Without, it is the program as it is (what the installers download and verify).
    /// </summary>
    [HttpGet("download/{platform}")]
    public IActionResult Download(string platform, [FromQuery] string? code, [FromQuery] string? machine)
    {
        Response.Headers.CacheControl = "no-store";
        if (code is null && machine is null)
        {
            var found = _downloads.Open(platform) ?? throw new RejectedException(Mnemarr.Core.Problem.NotFound, "This Mnemarr has no collector program for that system.");
            return PhysicalFile(found.Path, "application/octet-stream", found.Download.FileName);
        }

        var (server, normalised, name) = Checked(code, machine);
        // Anonymous, and a program is several megabytes: only a code that a person approved for this machine learns anything or gets one, and it is streamed.
        _enrolments.RequireApproved(normalised, name);
        var personalised = _downloads.Personalised(platform, new EmbeddedSetting(server, name, normalised, Insecure: server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            ?? throw new RejectedException(Mnemarr.Core.Problem.NotFound, "This Mnemarr has no collector program for that system.");
        return new PersonalisedProgramResult(personalised.Path, personalised.Trailer, personalised.FileName);
    }

    [HttpGet("install.ps1")]
    public IActionResult Windows([FromQuery] string? code, [FromQuery] string? machine)
    {
        var (server, normalised, name) = Checked(code, machine);
        var download = _downloads.Find("windows-x86_64") ?? throw new RejectedException(Mnemarr.Core.Problem.NotFound, "This Mnemarr has no collector program for Windows.");
        return Script(InstallScripts.Windows(server, normalised, name, download.Sha256));
    }

    [HttpGet("install.sh")]
    public IActionResult Unix([FromQuery] string? code, [FromQuery] string? machine)
    {
        var (server, normalised, name) = Checked(code, machine);
        var downloads = _downloads.Available().Where(download => download.Platform != "windows-x86_64").ToList();
        if (downloads.Count == 0)
        {
            throw new RejectedException(Mnemarr.Core.Problem.NotFound, "This Mnemarr has no collector program for Linux or macOS.");
        }

        return Script(InstallScripts.Unix(server, normalised, name, downloads));
    }

    private (string Server, string Code, string Machine) Checked(string? code, string? machine)
    {
        var normalised = EnrolmentCodes.Normalise(code) ?? throw new RejectedException(Mnemarr.Core.Problem.Invalid, "The code is not one.");
        if (!MachineNames.IsValid(machine))
        {
            throw new RejectedException(Mnemarr.Core.Problem.Invalid, "The name of the machine is not valid.");
        }

        var server = _commands.Server(Request);
        if (!InstallScripts.IsSafeAddress(server))
        {
            throw new RejectedException(Mnemarr.Core.Problem.Invalid, "The address of this Mnemarr cannot be written in an installer: set Collectors:PublicUrl.");
        }

        return (server, $"{normalised[..4]}-{normalised[4..8]}-{normalised[8..]}", machine!);
    }

    private ContentResult Script(string text)
    {
        Response.Headers.CacheControl = "no-store";
        return Content(text, PlainText);
    }
}

/// <summary>The program streamed from its file with the setting written after it: nothing of the program is held in memory.</summary>
internal sealed class PersonalisedProgramResult(string path, byte[] trailer, string fileName) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        var cancellationToken = context.HttpContext.RequestAborted;
        await using var program = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        response.ContentType = "application/octet-stream";
        response.ContentLength = program.Length + trailer.Length;
        response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = fileName }.ToString();
        await program.CopyToAsync(response.Body, cancellationToken);
        await response.Body.WriteAsync(trailer, cancellationToken);
    }
}
