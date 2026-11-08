using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Api.V1.Chat;
using Mnemarr.Core.Chat;
using Mnemarr.Core.Health;

namespace Mnemarr.Integration.Test;

/// <summary>What the readiness probe says about the assistant runner (ADR 0035, D-13): nothing is wrong when the chat is off, and a reason when it is on and cannot work.</summary>
public sealed class AgentHealthCheckFixture
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (AgentHealthCheck Check, FakeRunner Runner, FakeTimeProvider Clock) Build(bool enabled = true)
    {
        var runner = new FakeRunner();
        var options = Options.Create(new AgentOptions { Url = enabled ? "http://runner.example:8489" : null, Secret = new string('s', 40) });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero));
        return (new AgentHealthCheck(new AgentRunnerClient(new HttpClient(runner), options), options, clock), runner, clock);
    }

    private static Task<HealthCheckResult> CheckAsync(AgentHealthCheck check) => check.CheckHealthAsync(new HealthCheckContext(), Token);

    [Fact]
    public async Task should_find_nothing_wrong_when_the_chat_is_off()
    {
        var (check, runner, _) = Build(enabled: false);

        (await CheckAsync(check)).Status.Should().Be(HealthStatus.Healthy);
        runner.Requests.Should().BeEmpty("a chat that is off is not probed");
    }

    [Fact]
    public async Task should_be_healthy_when_the_runner_answers_and_one_agent_can_run()
    {
        var (check, _, _) = Build();

        (await CheckAsync(check)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_say_degraded_with_a_reason_when_the_runner_does_not_answer()
    {
        var (check, runner, _) = Build();
        runner.Down = true;

        var result = await CheckAsync(check);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data[HealthReasons.Key].Should().Be("agent-unreachable");
    }

    [Fact]
    public async Task should_say_degraded_with_a_reason_when_the_runner_refuses_the_secret()
    {
        var (check, runner, _) = Build();
        runner.Refuses = HttpStatusCode.Unauthorized;

        var result = await CheckAsync(check);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data[HealthReasons.Key].Should().Be("agent-secret-refused");
    }

    [Fact]
    public async Task should_ask_the_runner_once_in_half_a_minute()
    {
        var (check, runner, clock) = Build();

        await CheckAsync(check);
        await CheckAsync(check);
        runner.Requests.Should().HaveCount(1);

        clock.Advance(TimeSpan.FromSeconds(31));
        await CheckAsync(check);
        runner.Requests.Should().HaveCount(2);
    }
}
