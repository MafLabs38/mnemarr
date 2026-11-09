using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>What a collector says it found where it looks: a machine that is online and sends nothing says why on the machines page.</summary>
public sealed class WatchingApiFixture : IClassFixture<MnemarrFactory>
{
    private readonly MnemarrFactory _factory;

    public WatchingApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Admin, string Name)> MachineAsync(string? watching)
    {
        var callers = await Callers.ForAsync(_factory);
        var name = Callers.Unique("pc");
        var created = await callers.Admin.PostAsJsonAsync("/api/v1/enrolments", new { machine = name, kind = "intermittent" }, Token);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString();
        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync("/api/v1/ingest/enrol", new { code, machine = name }, Token);
        using var machine = _factory.CreateKeyClient((await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("key").GetString()!);
        (await machine.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "1.0.0", pendingLines = 0, watching }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return (callers.Admin, name);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient admin, string name) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token)).EnumerateArray().Single(item => item.GetProperty("machine").GetString() == name);

    [Fact]
    public async Task should_show_what_the_collector_found_where_it_looks()
    {
        var (admin, name) = await MachineAsync("Claude Code: the folder of its conversations is not there");

        (await StatusAsync(admin, name)).GetProperty("watching").GetString().Should().Be("Claude Code: the folder of its conversations is not there");
    }

    [Fact]
    public async Task should_keep_it_short_and_say_nothing_for_a_collector_that_says_nothing()
    {
        var (admin, long_) = await MachineAsync(new string('x', 1000));
        var (_, none) = await MachineAsync(null);

        (await StatusAsync(admin, long_)).GetProperty("watching").GetString()!.Length.Should().BeLessThanOrEqualTo(300);
        (await StatusAsync(admin, none)).GetProperty("watching").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
