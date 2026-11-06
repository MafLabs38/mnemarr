using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Titles;

namespace Mnemarr.Api.V1.Conversations;

/// <summary>The title of a conversation, where it comes from (<c>first-sentence</c>, <c>summary</c> written by a model, or <c>person</c>), and its summary when a model wrote one.</summary>
public sealed record TitleResource(string Path, string Title, string Source, string? Summary, bool ModelEnabled);

public sealed record SetTitleRequest(string? Path, string? Title);

public sealed record RewriteTitleRequest(string? Path);

public sealed record RewriteAllRequest(bool? IncludePerson);

/// <summary>How many conversations have a title of each origin, and whether a model writes titles.</summary>
public sealed record TitlesResource(bool ModelEnabled, int FirstSentence, int Summary, int Person);

public sealed record QueuedResource(int Queued);

/// <summary>
/// The titles of the conversations (ADR 0028). A conversation is addressed by the path of its note, which is what the person is looking at. Reading
/// them needs the right to read conversations; changing them, or having a model write them again, needs the right to write them.
/// </summary>
[ApiController]
[Route("api/v1/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationStore _store;
    private readonly ConversationTitles _titles;
    private readonly TitleWriting _writing;

    public ConversationsController(IConversationStore store, ConversationTitles titles, TitleWriting writing)
    {
        _store = store;
        _titles = titles;
        _writing = writing;
    }

    [HttpGet("title")]
    [Authorize(Policy = Permissions.ConversationsRead)]
    public TitleResource Get([FromQuery] string? path)
    {
        var record = Find(path);
        return Resource(record, _titles.TitleOf(record));
    }

    /// <summary>A person gives a conversation a title of their own: the note is written again with it, and no model replaces it.</summary>
    [HttpPut("title")]
    [Authorize(Policy = Permissions.ConversationsWrite)]
    public async Task<TitleResource> Set(SetTitleRequest request, CancellationToken cancellationToken)
    {
        var record = Find(request.Path);
        var changed = await _titles.SetAsync(record.Machine, record.Session, request.Title ?? string.Empty, TitleSources.Person, null, cancellationToken)
            ?? throw new RejectedException(Mnemarr.Core.Problem.Conflict, "The title could not be changed.");
        return Resource(changed, changed.Title ?? string.Empty);
    }

    /// <summary>Has the model write the title and the summary of one conversation again, now.</summary>
    [HttpPost("title/rewrite")]
    [Authorize(Policy = Permissions.ConversationsWrite)]
    public async Task<TitleResource> Rewrite(RewriteTitleRequest request, CancellationToken cancellationToken)
    {
        var changed = await _writing.RewriteAsync(request.Path ?? string.Empty, cancellationToken);
        return Resource(changed, changed.Title ?? string.Empty);
    }

    [HttpGet("titles")]
    [Authorize(Policy = Permissions.ConversationsRead)]
    public TitlesResource Counts()
    {
        var counts = _store.TitleCounts();
        return new TitlesResource(_writing.Enabled, counts.FirstSentence, counts.Summary, counts.Person);
    }

    /// <summary>Puts the conversations back to "only a first sentence", so that the model writes their titles again in the background (a person's are kept unless asked).</summary>
    [HttpPost("titles/rewrite")]
    [Authorize(Policy = Permissions.ConversationsWrite)]
    [ProducesResponseType<QueuedResource>(StatusCodes.Status202Accepted)]
    public IActionResult RewriteAll(RewriteAllRequest request)
    {
        if (!_writing.Enabled)
        {
            throw new RejectedException(Mnemarr.Core.Problem.Invalid, "Titles written by a model are not turned on.");
        }

        return Accepted(new QueuedResource(_store.RequestAgain(request.IncludePerson ?? false)));
    }

    private ConversationRecord Find(string? path) =>
        string.IsNullOrWhiteSpace(path) ? throw new RejectedException(Mnemarr.Core.Problem.Invalid, "The path of the note is needed.") : _store.FindByNote(path) ?? throw new RejectedException(Mnemarr.Core.Problem.NotFound, "There is no such conversation.");

    private TitleResource Resource(ConversationRecord record, string title) => new(record.NotePath, title, record.TitleSource, record.Summary, _writing.Enabled);
}
