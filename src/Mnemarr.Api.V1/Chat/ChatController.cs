using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Chat;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Api.V1.Chat;

/// <summary>A model an agent offers.</summary>
public sealed record ChatModelResource(string Id, string Label);

/// <summary>An agent the runner offers: <c>Available</c> is false when the runner has no credentials for it.</summary>
public sealed record ChatAgentResource(string Id, string Name, bool Available, string? DefaultModel, IReadOnlyList<ChatModelResource> Models);

/// <summary>
/// Whether the chat is on and which agents the runner offers; <c>Reachable</c> is false when it does not answer or answers something Mnemarr does not understand. <c>McpWriteTools</c>
/// says whether the tools that change notes are offered: without them the chat only reads.
/// </summary>
public sealed record ChatStatusResource(bool Enabled, bool Reachable, IReadOnlyList<ChatAgentResource>? Agents, bool McpWriteTools = false);

/// <summary>A new conversation: the agent (<c>claude-code</c>, <c>openai</c>) and its model, both optional.</summary>
public sealed record ChatConversationRequest(string? Agent, string? Model, string? Title);

/// <summary>The person's message, typed or pasted (several lines), and optionally another model for this turn on.</summary>
public sealed record ChatMessageRequest(string Text, string? Model);

/// <summary>The person's answer to a change the agent proposes.</summary>
public sealed record ChatPermissionRequest([property: System.Text.Json.Serialization.JsonRequired] bool Approve);

/// <summary>An empty JSON body, so that every write to the chat is a JSON request (never a cross-site form post).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2094:Classes should not be empty", Justification = "Its type is the contract: a body bound to it must be JSON, which a cross-site form post cannot send.")]
public sealed record ChatCancelRequest;

/// <summary>
/// The chat (ADR 0035): a proxy to the optional assistant runner, which runs a model whose only tool is Mnemarr's own MCP server. Off (404) without <c>MNEMARR__AGENT__URL</c>. It needs
/// <c>chat.use</c>; a conversation belongs to the person who started it (list, read, send, answer, cancel, events and delete are theirs), and each message carries a short-lived token
/// of that person, so that what the model does is what the person may do.
/// </summary>
[ApiController]
[Authorize(Policy = Permissions.ChatUse)]
[PeopleOnly]
[Route("api/v1/chat")]
public sealed partial class ChatController : ControllerBase
{
    public const int MaxMessageLength = 20_000;

    private readonly AgentRunnerClient _runner;
    private readonly ChatService _chat;
    private readonly AgentOptions _options;
    private readonly McpOptions _mcp;
    private readonly ILogger<ChatController> _logger;

    public ChatController(AgentRunnerClient runner, ChatService chat, IOptions<AgentOptions> options, IOptions<McpOptions> mcp, ILogger<ChatController> logger)
    {
        _runner = runner;
        _chat = chat;
        _options = options.Value;
        _mcp = mcp.Value;
        _logger = logger;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();

    /// <summary>Whether the chat is on, and the agents and models the runner offers.</summary>
    [HttpGet]
    [EndpointSummary("Whether the chat is on, and the agents and models it offers.")]
    public async Task<ChatStatusResource> GetAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new ChatStatusResource(false, false, null);
        }

        try
        {
            var response = await _runner.SendAsync(HttpMethod.Get, "v1/agents", null, cancellationToken);
            return response is { StatusCode: StatusCodes.Status200OK, Json: { } json } && ParseAgents(json) is { } agents
                ? new ChatStatusResource(true, true, agents, _mcp.WriteTools)
                : new ChatStatusResource(true, false, null, _mcp.WriteTools);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(exception.GetType().Name);
            return new ChatStatusResource(true, false, null, _mcp.WriteTools);
        }
    }

    /// <summary>The conversations of the person, latest first.</summary>
    [HttpGet("conversations")]
    [EndpointSummary("The person's conversations, latest first.")]
    public Task<IActionResult> ListAsync(CancellationToken cancellationToken) => RunAsync(() => _chat.ListAsync(Caller(), cancellationToken), cancellationToken);

    [HttpPost("conversations")]
    [EndpointSummary("Starts a conversation with an agent and a model.")]
    public Task<IActionResult> CreateAsync([FromBody] ChatConversationRequest request, CancellationToken cancellationToken)
    {
        if (!ValidName(request.Agent) || !ValidName(request.Model) || request.Title?.Length > 200)
        {
            return Task.FromResult<IActionResult>(Invalid("agent and model are short lowercase names; title is 200 characters at most."));
        }

        return RunAsync(() => _chat.CreateAsync(request, Caller(), cancellationToken), cancellationToken);
    }

    /// <summary>A conversation with its stored events (the history the page shows).</summary>
    [HttpGet("conversations/{id}")]
    [EndpointSummary("A conversation and its history.")]
    public async Task<IActionResult> GetConversationAsync(string id, CancellationToken cancellationToken) =>
        _chat.CanAccess(id, Caller()) ? await ForwardAsync(HttpMethod.Get, $"v1/conversations/{id}", null, cancellationToken) : NotFound();

    [HttpDelete("conversations/{id}")]
    [EndpointSummary("Deletes a conversation (its process stops).")]
    public async Task<IActionResult> DeleteAsync(string id, CancellationToken cancellationToken) =>
        _chat.CanAccess(id, Caller()) ? await RunAsync(() => _chat.DeleteAsync(id, cancellationToken), cancellationToken) : NotFound();

    /// <summary>Sends the person's message; the answer arrives on the event stream.</summary>
    /// <response code="409">A turn is running.</response>
    /// <response code="429">Too many conversations are running at once.</response>
    [HttpPost("conversations/{id}/messages")]
    [EndpointSummary("Sends a message; the answer arrives on the event stream.")]
    public async Task<IActionResult> SendAsync(string id, [FromBody] ChatMessageRequest request, CancellationToken cancellationToken)
    {
        if (!_chat.CanAccess(id, Caller()))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxMessageLength || !ValidName(request.Model))
        {
            return Invalid($"text is 1 to {MaxMessageLength} characters; model a short lowercase name.");
        }

        return await RunAsync(() => _chat.SendAsync(id, request.Text, request.Model, Caller(), cancellationToken), cancellationToken);
    }

    /// <summary>Approves or rejects a change the agent proposes. Approving sends it as it was shown; what it may do is what the person's role allows, since the call is made with their token.</summary>
    [HttpPost("conversations/{id}/permissions/{requestId}")]
    [EndpointSummary("Approves or rejects a change the agent proposes.")]
    public async Task<IActionResult> AnswerAsync(string id, string requestId, [FromBody] ChatPermissionRequest request, CancellationToken cancellationToken) =>
        RequestIdPattern().IsMatch(requestId) && _chat.CanAccess(id, Caller())
            ? await ForwardAsync(HttpMethod.Post, $"v1/conversations/{id}/permissions/{requestId}", request, cancellationToken)
            : NotFound();

    /// <summary>Stops the running turn; the changes that wait are rejected.</summary>
    [HttpPost("conversations/{id}/cancel")]
    [EndpointSummary("Stops the running turn.")]
    public async Task<IActionResult> CancelAsync(string id, [FromBody] ChatCancelRequest request, CancellationToken cancellationToken) =>
        _chat.CanAccess(id, Caller()) ? await ForwardAsync(HttpMethod.Post, $"v1/conversations/{id}/cancel", request, cancellationToken) : NotFound();

    /// <summary>The conversation's events as server-sent events: the stored history after <c>after</c>, then the live ones.</summary>
    /// <remarks>Each event carries its sequence number as the SSE id: EventSource resumes from it after a reconnect.</remarks>
    [HttpGet("conversations/{id}/events")]
    [EnableRateLimiting(RateLimits.ChatStreams)]
    [Produces("text/event-stream")]
    [EndpointSummary("The conversation's events, as server-sent events.")]
    public async Task StreamAsync(string id, [FromQuery] int after, [FromHeader(Name = "Last-Event-ID")] string? lastEventId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_chat.CanAccess(id, Caller()))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            using var upstream = await _runner.OpenStreamAsync(
                $"v1/conversations/{id}/events?after={Math.Max(0, after)}",
                int.TryParse(lastEventId, out var seq) && seq >= 0 ? seq.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                cancellationToken);
            if (!upstream.IsSuccessStatusCode)
            {
                Response.StatusCode = (int)upstream.StatusCode == StatusCodes.Status404NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status502BadGateway;
                return;
            }

            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page closed the stream.
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            // A runner that did not send its headers in time ends here too (a timeout is a cancellation that is not the page's).
            LogUnreachable(exception.GetType().Name);
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private static bool ValidName(string? value) => value is null || NamePattern().IsMatch(value);

    private ChatCaller Caller() => new(User.Person());

    private ObjectResult Invalid(string detail) => Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    private Task<IActionResult> ForwardAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        RunAsync(() => _runner.SendAsync(method, path, body, cancellationToken), cancellationToken);

    /// <summary>The runner's agents as Mnemarr's own contract, or null when it answers something that is not a list of agents.</summary>
    private IReadOnlyList<ChatAgentResource>? ParseAgents(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ChatAgentResource>>(json, JsonSerializerOptions.Web)?.Select(agent => agent with { Models = agent.Models ?? [] }).ToList();
        }
        catch (JsonException exception)
        {
            LogUnreachable(exception.GetType().Name);
            return null;
        }
    }

    /// <summary>Calls the runner and passes its answer through; a runner that does not answer, or refuses the secret, is a 502.</summary>
    private async Task<IActionResult> RunAsync(Func<Task<AgentRunnerResponse>> call, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        try
        {
            var response = await call();
            if (response.StatusCode == StatusCodes.Status401Unauthorized)
            {
                // The runner refused Mnemarr's secret: a configuration error, not the person's.
                LogUnreachable("secret refused");
                return Problem("The assistant runner refused Mnemarr's secret (MNEMARR__AGENT__SECRET).", statusCode: StatusCodes.Status502BadGateway);
            }

            return response.Json is null
                ? StatusCode(response.StatusCode)
                : new ContentResult { StatusCode = response.StatusCode, Content = response.Json, ContentType = "application/json" };
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(exception.GetType().Name);
            return Problem("The assistant runner did not answer.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The assistant runner did not answer ({Error})")]
    private partial void LogUnreachable(string error);
}
