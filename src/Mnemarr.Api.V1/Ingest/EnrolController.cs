using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Api.V1.Ingest;

/// <summary>The code a person gave the machine, and the name it was made for.</summary>
public sealed record EnrolRequest(string? Code, string? Machine, string? ComputerId = null);

/// <summary>What a machine is given when it joins: its key (shown once), its name and its kind.</summary>
public sealed record EnrolResult(string Key, string Machine, string Kind);

/// <summary>Where a machine exchanges the code it was given for its key (ADR 0027 §4): under the ingest prefix, which a proxy may publish for the collectors alone.</summary>
[ApiController]
[Route("api/v1/ingest/enrol")]
public sealed class EnrolController : ControllerBase
{
    /// <summary>A code and a name of a machine: nothing anonymous is given more room than that.</summary>
    public const int MaxRequestBytes = 4096;

    private readonly EnrolmentService _enrolments;
    private readonly ConversationRelocator _relocator;

    public EnrolController(EnrolmentService enrolments, ConversationRelocator relocator)
    {
        _relocator = relocator;
        _enrolments = enrolments;
    }

    /// <summary>
    /// Exchanges the code a person approved for the key of a machine. Anonymous: the code, which is single use, short-lived and bound to a
    /// machine name, is all that is needed; a wrong one is counted for the whole server and gets no hint of what was wrong.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<EnrolResult> Enrol(EnrolRequest request, CancellationToken cancellationToken)
    {
        var enrolled = _enrolments.Exchange(request.Code, request.Machine, request.ComputerId);
        if (enrolled.RenamedFrom is { } renamedFrom)
        {
            await _relocator.MoveAllAsync(renamedFrom, enrolled.Machine, cancellationToken);
        }

        return new EnrolResult(enrolled.Key, enrolled.Machine, MachineKinds.Name(enrolled.Kind));
    }
}
