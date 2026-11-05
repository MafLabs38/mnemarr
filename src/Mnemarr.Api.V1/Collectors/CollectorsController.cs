using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Api.V1.Collectors;

/// <summary>A machine that sent conversations, and when it last did: an agent that is silent is not an agent that has nothing to say.</summary>
public sealed record MachineResource(string Machine, int Conversations, long Lines, DateTimeOffset LastSeen);

/// <summary>
/// What may be seen of the collectors: the machines that sent conversations, and when. It tells something about the conversations (who held some, how many lines), so it needs the right to read them, like the conversations themselves. It is not under the ingest prefix, which is the
/// one a proxy may publish without a single sign-on for the collectors alone (Q-28).
/// </summary>
[ApiController]
[Route("api/v1/collectors")]
public sealed class CollectorsController : ControllerBase
{
    private readonly IConversationStore _store;

    public CollectorsController(IConversationStore store)
    {
        _store = store;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.ConversationsRead)]
    public IReadOnlyList<MachineResource> Machines() => [.. _store.Machines().Select(machine => new MachineResource(machine.Machine, machine.Conversations, machine.Lines, machine.LastSeen))];
}
