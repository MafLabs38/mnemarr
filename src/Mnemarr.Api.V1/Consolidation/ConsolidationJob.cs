using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Api.V1.Chat;
using Mnemarr.Core.Chat;
using Mnemarr.Core.Consolidation;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Api.V1.Consolidation;

/// <summary>A job that failed, with the short reason that is kept for the page (never what was read or written).</summary>
public sealed class ConsolidationFailureException : Exception
{
    public ConsolidationFailureException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// One run of the model (ADR 0036): the assistant runner starts a job conversation under the policy of an unattended job, the model is given the instructions and the excerpt with a token of the
/// consolidation account, and what it did is read from the conversation when it ends. The job conversation and the token are removed afterwards.
/// </summary>
public sealed class ConsolidationJob
{
    private static readonly string[] WriteTools = ["create_note", "update_note", "append_to_note", "rename_note"];
    private const int MaxFailureChars = 200;

    private readonly AgentRunnerClient _runner;
    private readonly ChatAccess _access;
    private readonly ConsolidationAccount _account;
    private readonly IOptions<ConsolidationOptions> _options;
    private readonly IOptions<AgentOptions> _agent;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConsolidationJob> _logger;

    public ConsolidationJob(AgentRunnerClient runner, ChatAccess access, ConsolidationAccount account, IOptions<ConsolidationOptions> options, IOptions<AgentOptions> agent, TimeProvider clock, ILogger<ConsolidationJob> logger)
    {
        _runner = runner;
        _access = access;
        _account = account;
        _options = options;
        _agent = agent;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Whether there is an assistant runner to give the job to.</summary>
    public bool RunnerConfigured => _agent.Value.Enabled;

    /// <summary>Gives the prompt to a model and returns how many notes it wrote or changed. Throws <see cref="ConsolidationFailureException"/> when the job fails, and <see cref="HttpRequestException"/> when the runner cannot be reached.</summary>
    public async Task<int> RunAsync(string prompt, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var created = await _runner.SendAsync(HttpMethod.Post, "v1/conversations", new { agent = string.IsNullOrEmpty(options.Agent) ? null : options.Agent, model = options.Model, title = "consolidation", policy = "job" }, cancellationToken);
        if (created is not { StatusCode: StatusCodes.Status201Created, Json: { } createdJson } || JsonNode.Parse(createdJson)?["id"]?.GetValue<string>() is not { } id)
        {
            throw new ConsolidationFailureException(Refused(created.StatusCode, "start a job"));
        }

        var job = ChatAccess.JobPrefix + id;
        try
        {
            var token = _access.Mint(_account.Ensure().Id, job);
            var sent = await _runner.SendAsync(HttpMethod.Post, $"v1/conversations/{id}/messages", new { Text = prompt, Model = options.Model, McpToken = token }, cancellationToken);
            if (sent.StatusCode != StatusCodes.Status202Accepted)
            {
                throw new ConsolidationFailureException(Refused(sent.StatusCode, "send the job"));
            }

            return await WaitAsync(id, cancellationToken);
        }
        finally
        {
            _access.Forget(job);
            try
            {
                await _runner.SendAsync(HttpMethod.Delete, $"v1/conversations/{id}", null, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "A consolidation job could not be removed from the runner.");
            }
        }
    }

    /// <summary>Waits for the job to end, and reads from what it did how many notes it wrote or changed. A job that ends with an error, or that does not end, is a failure.</summary>
    private async Task<int> WaitAsync(string id, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromMinutes(_options.Value.TimeoutMinutes);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.Value.PollSeconds), cancellationToken);
            var state = await _runner.SendAsync(HttpMethod.Get, $"v1/conversations/{id}", null, cancellationToken);
            if (state is not { StatusCode: StatusCodes.Status200OK, Json: { } json } || JsonNode.Parse(json) is not JsonObject conversation)
            {
                throw new ConsolidationFailureException(Refused(state.StatusCode, "follow the job"));
            }

            if (conversation["busy"]?.GetValue<bool>() != true)
            {
                return Evaluate(conversation["events"] as JsonArray ?? []);
            }

            if (_clock.GetUtcNow() > deadline)
            {
                throw new ConsolidationFailureException("The job took too long and was stopped.");
            }
        }
    }

    /// <summary>The notes written or changed (a call of a note tool that worked), or the failure the job ended with. Nothing of what was said is kept: only a count and a short reason.</summary>
    private static int Evaluate(JsonArray events)
    {
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var written = 0;
        string? error = null;
        var ended = false;
        foreach (var node in events.OfType<JsonObject>())
        {
            switch (node["type"]?.GetValue<string>())
            {
                case "tool-call" when node["id"]?.GetValue<string>() is { } callId && node["name"]?.GetValue<string>() is { } name:
                    calls[callId] = name;
                    break;
                case "tool-result" when node["isError"]?.GetValue<bool>() != true && node["id"]?.GetValue<string>() is { } resultId && calls.TryGetValue(resultId, out var tool) && WriteTools.Any(write => tool.EndsWith("__" + write, StringComparison.Ordinal)):
                    written++;
                    break;
                case "error":
                    error ??= Short(node["message"]?.GetValue<string>());
                    break;
                case "turn-end":
                    ended = true;
                    if (node["isError"]?.GetValue<bool>() == true)
                    {
                        error ??= "The job ended with an error.";
                    }

                    break;
            }
        }

        if (error is not null)
        {
            throw new ConsolidationFailureException(error);
        }

        return ended ? written : throw new ConsolidationFailureException("The job ended without an answer.");
    }

    private static string Short(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "The job failed.";
        }

        return text.Length <= MaxFailureChars ? text : text[..MaxFailureChars];
    }

    private static string Refused(int status, string what) => $"The assistant runner would not {what} (status {status}).";
}
