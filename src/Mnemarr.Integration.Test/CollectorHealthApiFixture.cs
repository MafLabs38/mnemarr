using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Health;

namespace Mnemarr.Integration.Test;

/// <summary>The states of a machine told from the clock, and the health check they raise. Apart from the other tests: they move time, and a machine that is silent degrades the health of the whole host.</summary>
public sealed class CollectorHealthApiFixture : EnrolmentFixtureBase, IClassFixture<EnrolledOnlyFactory>
{
    // A machine that stays always-on and goes silent degrades the health of the whole host, and these tests move the clock: each leaves nothing behind.
    private static async Task RevokeAsync(HttpClient admin, params string[] machines)
    {
        foreach (var machine in machines)
        {
            await admin.DeleteAsync($"/api/v1/collectors/{machine}", Token);
        }
    }

    private async Task<HealthCheckResult> HealthAsync() =>
        await new CollectorsHealthCheck(_factory.Services.GetRequiredService<CollectorService>()).CheckHealthAsync(new HealthCheckContext(), Token);

    public CollectorHealthApiFixture(EnrolledOnlyFactory factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task should_tell_a_machine_online_then_away_when_it_sleeps_and_an_always_on_one_silent_and_raise_the_health_check_only_for_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var sleeper = Callers.Unique("laptop");
        var server = Callers.Unique("server");
        using var sleeperKey = await JoinAsync(callers.Admin, sleeper, "intermittent");
        using var serverKey = await JoinAsync(callers.Admin, server, "always-on");
        (await StatusOfAsync(callers.Admin, sleeper)).GetProperty("state").GetString().Should().Be("waiting");
        foreach (var key in new[] { sleeperKey, serverKey })
        {
            (await key.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.1.0", pendingLines = 3 }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        var online = await StatusOfAsync(callers.Admin, sleeper);
        online.GetProperty("state").GetString().Should().Be("online");
        online.GetProperty("version").GetString().Should().Be("0.1.0");
        online.GetProperty("pendingLines").GetInt64().Should().Be(3);

        _factory.Clock.Advance(TimeSpan.FromMinutes(10));
        (await StatusOfAsync(callers.Admin, sleeper)).GetProperty("state").GetString().Should().Be("away");
        (await StatusOfAsync(callers.Admin, server)).GetProperty("state").GetString().Should().Be("silent");
        (await HealthAsync()).Status.Should().Be(HealthStatus.Healthy, "ten minutes of silence is not yet an alert");

        _factory.Clock.Advance(TimeSpan.FromMinutes(30));
        var silent = await HealthAsync();
        silent.Status.Should().Be(HealthStatus.Degraded, "an always-on machine has been silent for too long");
        silent.Data["reason"].Should().Be("collector-silent");
        (await serverKey.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.1.0", pendingLines = 0 }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await HealthAsync()).Status.Should().Be(HealthStatus.Healthy, "it spoke again, and the sleeper's absence never counted");
        await RevokeAsync(callers.Admin, sleeper, server);
    }

    [Fact]
    public async Task should_report_a_collector_that_says_it_is_failing_and_never_keep_a_path_or_more_than_a_sentence_of_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("server");
        using var key = await JoinAsync(callers.Admin, machine, "always-on");

        await key.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.1.0", pendingLines = 0, error = new string('x', 500) }, Token);

        var status = await StatusOfAsync(callers.Admin, machine);
        status.GetProperty("state").GetString().Should().Be("failing");
        status.GetProperty("error").GetString()!.Length.Should().BeLessThanOrEqualTo(200);
        var failing = await HealthAsync();
        failing.Status.Should().Be(HealthStatus.Degraded);
        failing.Data["reason"].Should().Be("collector-failing");
        await key.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.1.0", pendingLines = 0 }, Token);
        (await StatusOfAsync(callers.Admin, machine)).GetProperty("state").GetString().Should().Be("online");
        await RevokeAsync(callers.Admin, machine);
    }

    [Fact]
    public async Task should_tell_a_machine_paused_by_its_person_as_paused_and_never_raise_the_health_check_for_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("server");
        using var key = await JoinAsync(callers.Admin, machine, "always-on");

        await key.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.1.0", pendingLines = 0, paused = true }, Token);

        (await StatusOfAsync(callers.Admin, machine)).GetProperty("state").GetString().Should().Be("paused");
        _factory.Clock.Advance(TimeSpan.FromMinutes(3));
        await key.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.1.0", pendingLines = 0, paused = true }, Token);
        (await HealthAsync()).Status.Should().Be(HealthStatus.Healthy);

        await key.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.1.0", pendingLines = 0 }, Token);
        (await StatusOfAsync(callers.Admin, machine)).GetProperty("state").GetString().Should().Be("online", "an older collector says nothing of a pause, and a resumed one says it is not");
        await RevokeAsync(callers.Admin, machine);
    }
}
