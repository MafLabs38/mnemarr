using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Api.V1.Ingest;

/// <summary>A batch of new lines of one conversation, as a collector sends it (ADR 0024).</summary>
/// <param name="Format">What the lines are: <c>claude-code</c>.</param>
/// <param name="Machine">The machine the conversation was held on.</param>
/// <param name="Application">The application (the project) it was about.</param>
/// <param name="Session">The identifier of the conversation.</param>
/// <param name="From">The position, in the conversation file, of the first line sent: what was taken before is not sent again.</param>
/// <param name="Lines">The lines as they are in the file, one per entry, secrets already masked by the collector.</param>
public sealed record IngestConversationRequest(string Format, string Machine, string Application, string Session, [property: JsonRequired] long From, IReadOnlyList<string> Lines);

/// <summary>What the server took: how many lines, and the position of the next line it wants. A collector that is told a position other than the one it expected resumes from it.</summary>
public sealed record IngestResultResource(long Next, int Accepted);

/// <summary>
/// Where the collectors send what the assistants said. It takes a key of the <c>Collector</c> role (ingest only: such a key reads nothing),
/// and what comes in is kept, masked once more, and written as notes that can be searched at once.
/// </summary>
[ApiController]
[Route("api/v1/ingest")]
public sealed class IngestController : ControllerBase
{
    /// <summary>A batch of lines, with their JSON around them, is at most this big.</summary>
    public const int MaxRequestBytes = 8 * 1024 * 1024;

    private readonly ConversationIngestor _ingestor;
    private readonly CollectorService _collectors;

    public IngestController(ConversationIngestor ingestor, CollectorService collectors)
    {
        _ingestor = ingestor;
        _collectors = collectors;
    }

    [HttpPost("conversations")]
    [Authorize(Policy = Permissions.IngestWrite)]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IngestResultResource> Conversations(IngestConversationRequest request, CancellationToken cancellationToken)
    {
        var computer = _collectors.Authorize(User.AccountId(), request.Machine);
        var result = await _ingestor.IngestAsync(new IngestBatch(request.Format, request.Machine, request.Application, request.Session, request.From, request.Lines, User.ActorName(), computer), cancellationToken);
        return new IngestResultResource(result.Next, result.Accepted);
    }
}
