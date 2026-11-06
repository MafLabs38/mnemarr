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
public sealed record MachineResource(string Machine, string? Kind, string State, int Conversations, long Lines, DateTimeOffset? LastSeen, DateTimeOffset? EnrolledAt, string? Version, long PendingLines, string? Error)
{
    public static MachineResource From(MachineStatus status) => new(
        status.Name,
        status.Kind is { } kind ? MachineKinds.Name(kind) : null,
        status.State.ToString().ToLowerInvariant(),
        status.Conversations,
        status.Lines,
        status.LastSeen,
        status.EnrolledAt,
        status.Version,
        status.PendingLines,
        status.Error);
}

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

    public CollectorsController(CollectorService collectors)
    {
        _collectors = collectors;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.ConversationsRead)]
    public IReadOnlyList<MachineResource> Machines() => [.. _collectors.Machines().Select(MachineResource.From)];

    /// <summary>Takes a machine that was revoked out of the list, so that its name is free again. Its conversations stay.</summary>
    [HttpPost("{machine}/forget")]
    [Authorize(Policy = Permissions.CollectorsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Forget(string machine)
    {
        _collectors.Forget(machine, User.ActorName());
        return NoContent();
    }

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
