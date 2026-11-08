using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Collectors;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;

namespace Mnemarr.Api.V1.Mcp;

/// <summary>
/// The MCP server (F-MCP-01, ADR 0023): the Model Context Protocol over HTTP, one JSON-RPC message per POST, answered at once as JSON
/// (the "Streamable HTTP" transport without a stream: the server never starts a message of its own). An assistant connects with the API key of a
/// service account (<c>X-Api-Key</c>); what it sees and does is what that key may, as for the REST API.
/// </summary>
[ApiController]
[Route("mcp")]
[Authorize]
public sealed class McpController : ControllerBase
{
    private const string LatestProtocol = "2025-06-18";
    private static readonly string[] Protocols = [LatestProtocol, "2025-03-26", "2024-11-05"];
    private const int MaxRequestBytes = 4 * 1024 * 1024;

    private readonly McpTools _tools;
    private readonly InstallCommands _installs;

    public McpController(McpTools tools, InstallCommands installs)
    {
        _tools = tools;
        _installs = installs;
    }

    private McpCaller Caller => new(User.Scope(), User.ActorName(), permission => User.HasClaim(MnemarrClaims.Permission, permission), _installs.Server(Request), User.IsJob());

    [HttpPost]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        JsonElement message;
        try
        {
            using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Reply(StatusCodes.Status400BadRequest, Failure(null, -32700, "The message is not JSON."));
        }

        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
        {
            // A response of the client to a request of the server, or something that is not a message: the server sends no request, so there is nothing to take.
            return message.ValueKind == JsonValueKind.Object && message.TryGetProperty("result", out _) ? Accepted() : Reply(StatusCodes.Status400BadRequest, Failure(null, -32600, "Not a JSON-RPC request."));
        }

        var id = message.TryGetProperty("id", out var found) ? found.Clone() : (JsonElement?)null;
        if (id is null)
        {
            // A notification (initialized, cancelled…): nothing to answer.
            return Accepted();
        }

        var arguments = message.TryGetProperty("params", out var parameters) ? parameters : default;
        return Reply(StatusCodes.Status200OK, await HandleAsync(id.Value, method.GetString()!, arguments, cancellationToken));
    }

    /// <summary>This server never opens a stream and keeps no session: only POST.</summary>
    [HttpGet]
    [HttpDelete]
    public IActionResult NotOffered()
    {
        Response.Headers.Allow = "POST";
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    private async Task<JsonObject> HandleAsync(JsonElement id, string method, JsonElement arguments, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return Success(id, Initialize(arguments));
            case "ping":
                return Success(id, new JsonObject());
            case "tools/list":
                return Success(id, new JsonObject { ["tools"] = new JsonArray([.. _tools.Available(Caller).Select(tool => (JsonNode)new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["inputSchema"] = tool.Schema.DeepClone() })]) });
            case "tools/call":
                return await CallToolAsync(id, arguments, cancellationToken);
            default:
                return Failure(id, -32601, $"The method '{method}' is not offered.");
        }
    }

    private static JsonObject Initialize(JsonElement arguments)
    {
        var asked = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && Protocols.Contains(asked) ? asked : LatestProtocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "mnemarr", ["version"] = "1" },
            ["instructions"] = "Mnemarr is long-term memory made of Markdown notes. Start with list_sources, then search or read; write only where a source says it is writable.",
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonElement id, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return Failure(id, -32602, "A tool call needs a tool name.");
        }

        var toolArguments = arguments.TryGetProperty("arguments", out var given) ? given : default;
        try
        {
            var result = await _tools.CallAsync(name.GetString()!, toolArguments, Caller, cancellationToken);
            return Success(id, ToolResult(result.ToJsonString(), isError: false));
        }
        catch (RejectedException ex)
        {
            // A refusal is an answer the assistant can read and act on (the version is old, the source is read-only), not a failure of the protocol.
            return Success(id, ToolResult(ex.Message, isError: true));
        }
    }

    private static JsonObject ToolResult(string text, bool isError) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };

    private static JsonObject Success(JsonElement id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result };

    private static JsonObject Failure(JsonElement? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id is { } known ? JsonNode.Parse(known.GetRawText()) : null, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private ContentResult Reply(int status, JsonObject body) => new() { StatusCode = status, ContentType = "application/json", Content = body.ToJsonString() };
}
