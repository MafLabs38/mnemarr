using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Collectors;

/// <summary>
/// A machine as the operator sees it: what it is, the state told from the facts, and what it sent. An agent that is silent is not an agent
/// that has nothing to say, and a machine that sleeps is not a machine in error. The kind is <c>always-on</c> or <c>intermittent</c>, and null for a
/// name that conversations came from and that never joined; the state is <c>unenrolled</c>, <c>refused</c>, <c>waiting</c>, <c>failing</c>,
/// <c>online</c>, <c>away</c> or <c>silent</c> (ADR 0027 §7).
/// </summary>
public sealed record MachineResource(string Machine, string? Kind, string State, int Conversations, long Lines, DateTimeOffset? LastSeen, DateTimeOffset? EnrolledAt, string? Version, long PendingLines, string? Error, bool ProgramDiffers)
{
    /// <summary><paramref name="programDiffers"/>: this Mnemarr carries a program for the system of the machine, and it is not the program the machine runs.</summary>
    public static MachineResource From(MachineStatus status, bool programDiffers) => new(
        status.Name,
        status.Kind is { } kind ? MachineKinds.Name(kind) : null,
        status.State.ToString().ToLowerInvariant(),
        status.Conversations,
        status.Lines,
        status.LastSeen,
        status.EnrolledAt,
        status.Version,
        status.PendingLines,
        status.Error,
        programDiffers);
}

public sealed record RenameMachineRequest(string? Name);

public sealed record MergeMachineRequest(string? Into);

/// <summary>How many conversations were filed under the other machine, and how many it held already (not made twice).</summary>
public sealed record MergeResult(int Moved, int AlreadyThere);

public sealed record EraseResult(int Conversations);

/// <summary>
/// What may be seen of the collectors: the machines that joined or sent conversations, and what state they are in. It tells something about the
/// conversations (who held some, how many lines), so it needs the right to read them, like the conversations themselves. It is not under the
/// ingest prefix, which is the one a proxy may publish without a single sign-on for the collectors alone (Q-28).
/// </summary>
[ApiController]
[Route("api/v1/collectors")]
public sealed class CollectorsController : ControllerBase
{
    private readonly CollectorService _collectors;
    private readonly CollectorDownloads _downloads;

    public CollectorsController(CollectorService collectors, CollectorDownloads downloads)
    {
        _collectors = collectors;
        _downloads = downloads;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.ConversationsRead)]
    public IReadOnlyList<MachineResource> Machines() => [.. _collectors.Machines().Select(status => MachineResource.From(status, ProgramDiffers(status)))];

    private bool ProgramDiffers(MachineStatus status) =>
        status.Program is { } running && status.Platform is { } platform && _downloads.Find(platform) is { } carried && !string.Equals(carried.Sha256, running, StringComparison.Ordinal);

    /// <summary>Takes a machine that was revoked out of the list, so that its name is free again. Its conversations stay.</summary>
    [HttpPost("{machine}/forget")]
    [Authorize(Policy = Permissions.CollectorsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Forget(string machine)
    {
        _collectors.Forget(machine, User.ActorName());
        return NoContent();
    }

    /// <summary>Takes a machine out of the list, revoked or not. What it sent stays, filed under its name.</summary>
    [HttpPost("{machine}/remove")]
    [Authorize(Policy = Permissions.CollectorsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Remove(string machine)
    {
        _collectors.Remove(machine, User.ActorName());
        return NoContent();
    }

    /// <summary>Gives a machine (or a name that conversations came from) another name; what it sent is filed under the new one.</summary>
    [HttpPost("{machine}/rename")]
    [Authorize(Policy = Permissions.CollectorsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Rename(string machine, RenameMachineRequest request, CancellationToken cancellationToken)
    {
        await _collectors.RenameAsync(machine, request.Name ?? string.Empty, User.ActorName(), cancellationToken);
        return NoContent();
    }

    /// <summary>Files what a machine sent under another enrolled machine, without doubling a conversation both have, and takes it out of the list.</summary>
    [HttpPost("{machine}/merge")]
    [Authorize(Policy = Permissions.CollectorsManage)]
    public async Task<MergeResult> Merge(string machine, MergeMachineRequest request, CancellationToken cancellationToken)
    {
        var report = await _collectors.MergeAsync(machine, request.Into ?? string.Empty, User.ActorName(), cancellationToken);
        return new MergeResult(report.Moved, report.AlreadyThere);
    }

    /// <summary>Deletes a machine and every conversation it sent. It cannot be undone.</summary>
    [HttpPost("{machine}/erase")]
    [Authorize(Policy = Permissions.CollectorsManage)]
    public async Task<EraseResult> Erase(string machine, CancellationToken cancellationToken) =>
        new(await _collectors.EraseAsync(machine, User.ActorName(), cancellationToken));

    /// <summary>Revokes a machine: its keys stop working and its account is disabled. It can join again with a new code.</summary>
    [HttpDelete("{machine}")]
    [Authorize(Policy = Permissions.CollectorsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Revoke(string machine)
    {
        _collectors.Revoke(machine, User.ActorName());
        return NoContent();
    }
}
