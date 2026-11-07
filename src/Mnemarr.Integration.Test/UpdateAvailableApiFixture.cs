using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>A machine says which program it runs; this Mnemarr says whether it carries another one for that system (ADR 0031 §7).</summary>
public sealed class UpdateAvailableApiFixture : IClassFixture<DownloadsFactory>
{
    private readonly DownloadsFactory _factory;

    public UpdateAvailableApiFixture(DownloadsFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Admin, HttpClient Machine, string Name)> JoinAsync()
    {
        var callers = await Callers.ForAsync(_factory);
        var name = Callers.Unique("pc");
        var created = await callers.Admin.PostAsJsonAsync("/api/v1/enrolments", new { machine = name, kind = "intermittent" }, Token);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString();
        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync("/api/v1/ingest/enrol", new { code, machine = name }, Token);
        var key = (await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("key").GetString()!;
        return (callers.Admin, _factory.CreateKeyClient(key), name);
    }

    private static async Task<bool> UpdateAvailableAsync(HttpClient admin, string name) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token)).EnumerateArray().Single(item => item.GetProperty("machine").GetString() == name).GetProperty("updateAvailable").GetBoolean();

    private static Task<HttpResponseMessage> BeatAsync(HttpClient machine, string? platform, string? program) =>
        machine.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.0.0", pendingLines = 0, platform, program }, Token);

    [Fact]
    public async Task should_say_nothing_of_an_update_for_a_machine_that_runs_the_program_this_mnemarr_carries()
    {
        var (admin, machine, name) = await JoinAsync();

        (await BeatAsync(machine, "windows-x86_64", DownloadsFactory.Sha256Of("the windows program"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await UpdateAvailableAsync(admin, name)).Should().BeFalse();
    }

    [Fact]
    public async Task should_say_an_update_is_available_for_a_machine_that_runs_another_program_than_the_one_this_mnemarr_carries()
    {
        var (admin, machine, name) = await JoinAsync();

        await BeatAsync(machine, "windows-x86_64", DownloadsFactory.Sha256Of("an older program"));

        (await UpdateAvailableAsync(admin, name)).Should().BeTrue();
        await BeatAsync(machine, "windows-x86_64", DownloadsFactory.Sha256Of("the windows program"));
        (await UpdateAvailableAsync(admin, name)).Should().BeFalse("once it runs the program this Mnemarr carries, there is nothing to update");
    }

    [Fact]
    public async Task should_not_say_it_for_a_machine_that_does_not_say_what_it_runs_or_whose_system_has_no_program_here()
    {
        var (admin, machine, name) = await JoinAsync();
        var (_, other, otherName) = await JoinAsync();

        await BeatAsync(machine, null, null);
        await BeatAsync(other, "macos-aarch64", DownloadsFactory.Sha256Of("an older program"));

        (await UpdateAvailableAsync(admin, name)).Should().BeFalse("an older collector does not say, and nobody is told something that is not known");
        (await UpdateAvailableAsync(admin, otherName)).Should().BeFalse("this Mnemarr has no program for that system");
    }

    [Fact]
    public async Task should_ignore_a_program_that_is_not_a_hash()
    {
        var (admin, machine, name) = await JoinAsync();

        (await BeatAsync(machine, "windows-x86_64", "not-a-hash")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await UpdateAvailableAsync(admin, name)).Should().BeFalse();
    }
}
