using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Chat;

namespace Mnemarr.Api.V1.Chat;

/// <summary>Who is calling the chat: the person (their account), as the rules of ownership need them.</summary>
public sealed record ChatCaller(Account Account);

/// <summary>
/// The rules of the chat that sit between the controller and the runner (ADR 0035): a conversation belongs to the person who started it, and the list shows only their own;
/// a conversation is registered right after the runner creates it, and removed from the runner again if that fails; each message carries a short-lived token of the person, so
/// that the model's tool calls run as them. The controller maps the answers to HTTP.
/// </summary>
public sealed partial class ChatService
{
    private readonly AgentRunnerClient _runner;
    private readonly ChatAccess _access;
    private readonly ILogger<ChatService> _logger;
    private readonly int _maxConversationsPerPerson;

    public ChatService(AgentRunnerClient runner, ChatAccess access, IOptions<AgentOptions> options, ILogger<ChatService> logger)
    {
        _runner = runner;
        _access = access;
        _logger = logger;
        _maxConversationsPerPerson = options.Value.MaxConversationsPerPerson;
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex IdPattern();

    /// <summary>Whether the conversation exists for this person: a well-formed id and their own. Everything else is "no", never "forbidden": another person's conversation is not revealed.</summary>
    public bool CanAccess(string id, ChatCaller caller) => IdPattern().IsMatch(id) && _access.CanAccess(id, caller.Account.Id);

    /// <summary>The runner's conversations that the person owns.</summary>
    public async Task<AgentRunnerResponse> ListAsync(ChatCaller caller, CancellationToken cancellationToken)
    {
        var response = await _runner.SendAsync(HttpMethod.Get, "v1/conversations", null, cancellationToken);
        if (response is not { StatusCode: StatusCodes.Status200OK, Json: { } json } || ParseList(json) is not { } all)
        {
            return response.StatusCode == StatusCodes.Status200OK ? Malformed : response;
        }

        // The runner's list is what exists: what Mnemarr keeps of a conversation that is gone (the runner is full, or its volume was lost) goes with it.
        _access.Keep(all.Select(item => item.Id).ToHashSet(StringComparer.Ordinal));
        var mine = _access.OwnedBy(caller.Account.Id);
        var visible = new JsonArray();
        foreach (var item in all.Where(item => mine.Contains(item.Id)))
        {
            visible.Add(item.Node.DeepClone());
        }

        return new AgentRunnerResponse(StatusCodes.Status200OK, visible.ToJsonString());
    }

    private static AgentRunnerResponse Malformed => new(StatusCodes.Status502BadGateway, """{"title":"The assistant runner answered something Mnemarr does not understand: are the two images of the same version?"}""");

    /// <summary>The conversations of the runner's list, or null when it is not a list of objects that each have an id.</summary>
    private static List<(string Id, JsonObject Node)>? ParseList(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray all)
            {
                return null;
            }

            var items = new List<(string, JsonObject)>();
            foreach (var node in all)
            {
                if (node is not JsonObject item || item["id"] is not JsonValue value || !value.TryGetValue<string>(out var id))
                {
                    return null;
                }

                items.Add((id, item));
            }

            return items;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? ParseId(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject { } item && item["id"] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Starts a conversation and records its owner; when that fails, the runner's conversation is removed so that none is left without an owner.</summary>
    public async Task<AgentRunnerResponse> CreateAsync(object request, ChatCaller caller, CancellationToken cancellationToken)
    {
        // Two creations at the same time may both pass this check: the cap is there to keep one person from filling the runner, and one over is no harm.
        if (_access.OwnedBy(caller.Account.Id).Count >= _maxConversationsPerPerson)
        {
            return new AgentRunnerResponse(StatusCodes.Status429TooManyRequests, $$"""{"title":"You keep {{_maxConversationsPerPerson}} conversations: delete one first."}""");
        }

        var response = await _runner.SendAsync(HttpMethod.Post, "v1/conversations", request, cancellationToken);
        if (response is not { StatusCode: StatusCodes.Status201Created, Json: { } json })
        {
            return response;
        }

        if (ParseId(json) is not { } id)
        {
            return Malformed;
        }

        try
        {
            _access.Register(id, caller.Account);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRegisterFailed(exception.GetType().Name);
            await TryRemoveAsync(id);
            throw;
        }

        return response;
    }

    /// <summary>Deletes the runner's conversation and forgets its owner once the runner no longer has it.</summary>
    public async Task<AgentRunnerResponse> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var response = await _runner.SendAsync(HttpMethod.Delete, $"v1/conversations/{id}", null, cancellationToken);
        if (response.StatusCode is < 300 or StatusCodes.Status404NotFound)
        {
            _access.Forget(id);
        }

        return response;
    }

    /// <summary>Sends the message with a token tied to the person and the conversation, which the model's calls to the MCP server carry.</summary>
    public async Task<AgentRunnerResponse> SendAsync(string id, string text, string? model, ChatCaller caller, CancellationToken cancellationToken)
    {
        var token = _access.Mint(caller.Account.Id, id);
        try
        {
            var response = await _runner.SendAsync(HttpMethod.Post, $"v1/conversations/{id}/messages", new { Text = text, Model = model, McpToken = token }, cancellationToken);
            if (response.StatusCode >= 300)
            {
                // A message that the runner refused (a turn is running, the runner is full) starts no process: its token is of no use and must not pile up.
                _access.Revoke(token);
            }

            return response;
        }
        catch
        {
            _access.Revoke(token);
            throw;
        }
    }

    private async Task TryRemoveAsync(string id)
    {
        try
        {
            await _runner.SendAsync(HttpMethod.Delete, $"v1/conversations/{id}", null, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            LogCleanupFailed(exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A conversation of the chat was created but its owner could not be recorded ({Error}); removing it from the runner")]
    private partial void LogRegisterFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The conversation that could not be recorded could not be removed from the runner either ({Error})")]
    private partial void LogCleanupFailed(string error);
}
