using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Chat;
using Mnemarr.Core.Health;

namespace Mnemarr.Api.V1.Chat;

/// <summary>
/// The assistant runner of the chat (ADR 0035): off is not a fault (nobody asked for a chat); a runner that does not answer, that refuses the shared secret or that has no credentials for any of its agents
/// makes Mnemarr degraded, with the reason, so that a companion that watches it sees what a person would learn only by opening the panel. The probe is cached for half a minute.
/// </summary>
public sealed class AgentHealthCheck : IHealthCheck
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly AgentRunnerClient _runner;
    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private HealthCheckResult _last = HealthCheckResult.Healthy();

    public AgentHealthCheck(AgentRunnerClient runner, IOptions<AgentOptions> options, TimeProvider clock)
    {
        _runner = runner;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return HealthCheckResult.Healthy("The chat is off.");
        }

        lock (_lock)
        {
            if (_clock.GetUtcNow() - _checkedAt < CacheFor)
            {
                return _last;
            }
        }

        var result = await ProbeAsync(cancellationToken);
        lock (_lock)
        {
            _last = result;
            _checkedAt = _clock.GetUtcNow();
        }

        return result;
    }

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        AgentRunnerResponse answer;
        try
        {
            answer = await _runner.SendAsync(HttpMethod.Get, "v1/agents", null, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return HealthReasons.Degraded("agent-unreachable", "The assistant runner does not answer: check MNEMARR__AGENT__URL and that it runs.");
        }

        if (answer.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
        {
            return HealthReasons.Degraded("agent-secret-refused", "The assistant runner refused Mnemarr's secret: MNEMARR__AGENT__SECRET must equal the runner's MNEMARR_AGENT_SECRET.");
        }

        if (answer.StatusCode != StatusCodes.Status200OK)
        {
            return HealthReasons.Degraded("agent-unreachable", $"The assistant runner answered {answer.StatusCode}.");
        }

        return AnyAgentAvailable(answer.Json)
            ? HealthCheckResult.Healthy("The assistant runner answers.")
            : HealthReasons.Degraded("agent-no-credentials", "The assistant runner answers but none of its agents has credentials (CLAUDE_CODE_OAUTH_TOKEN, or an OpenAI-compatible address).");
    }

    private static bool AnyAgentAvailable(string? json)
    {
        try
        {
            return json is not null && JsonNode.Parse(json) is JsonArray agents && agents.OfType<JsonObject>().Any(agent => agent["available"] is JsonValue value && value.TryGetValue<bool>(out var available) && available);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
