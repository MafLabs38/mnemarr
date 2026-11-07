using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The updates of the collector that a Mnemarr carries (ADR 0032): what is served to a collector that asks, and who is told that an update is available.</summary>
public sealed class UpdatesApiFixture : IClassFixture<UpdatesFactory>, IClassFixture<DownloadsFactory>
{
    private const string Feed = "/api/v1/ingest/updates";
    private readonly UpdatesFactory _factory;
    private readonly DownloadsFactory _without;

    public UpdatesApiFixture(UpdatesFactory factory, DownloadsFactory without)
    {
        _factory = factory;
        _without = without;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task should_serve_the_feed_and_the_packages_to_anybody_without_keeping_what_changes_with_each_release()
    {
        var feed = await _factory.CreateBrowserClient().GetAsync($"{Feed}/releases.win.json", Token);
        var package = await _factory.CreateBrowserClient().GetAsync($"{Feed}/MnemarrCollector-1.4.0-full.nupkg", Token);

        feed.StatusCode.Should().Be(HttpStatusCode.OK);
        feed.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        feed.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await feed.Content.ReadAsStringAsync(Token)).Should().Contain("1.4.0");
        package.StatusCode.Should().Be(HttpStatusCode.OK);
        (await package.Content.ReadAsStringAsync(Token)).Should().Be("the package of 1.4.0");
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("secrets.json")]
    [InlineData("MnemarrCollector-9.9.9-full.nupkg")]
    [InlineData("..%2Freleases.win.json")]
    [InlineData("%2e%2e%2f%2e%2e%2fappsettings.json")]
    [InlineData("a%5Cb-Setup.exe")]
    public async Task should_serve_nothing_that_the_installer_did_not_make_and_nothing_outside_the_folder(string name)
    {
        var response = await _factory.CreateBrowserClient().GetAsync($"{Feed}/{name}", Token);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_offer_the_installer_to_those_who_have_none_and_say_so_when_there_is_none()
    {
        var installer = await _factory.CreateBrowserClient().GetAsync("/api/v1/ingest/installer/windows", Token);
        var none = await _without.CreateBrowserClient().GetAsync("/api/v1/ingest/installer/windows", Token);
        var noFeed = await _without.CreateBrowserClient().GetAsync($"{Feed}/releases.win.json", Token);

        installer.StatusCode.Should().Be(HttpStatusCode.OK);
        (await installer.Content.ReadAsStringAsync(Token)).Should().Be("the installer");
        installer.Content.Headers.ContentDisposition!.FileName.Should().Be("MnemarrCollector-win-Setup.exe");
        none.StatusCode.Should().Be(HttpStatusCode.NotFound);
        noFeed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_give_the_installer_in_the_lines_it_gives_for_a_new_machine()
    {
        var callers = await Callers.ForAsync(_factory);
        var created = await callers.Admin.PostAsJsonAsync("/api/v1/enrolments", new { machine = Callers.Unique("pc"), kind = "intermittent" }, Token);

        var windows = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("installs").EnumerateArray().Single(line => line.GetProperty("system").GetString() == "windows");

        windows.GetProperty("setupUrl").GetString().Should().EndWith("/api/v1/ingest/installer/windows");
    }

    private async Task<(HttpClient Admin, string Name)> MachineAsync(string version, bool canUpdate)
    {
        var callers = await Callers.ForAsync(_factory);
        var name = Callers.Unique("pc");
        var created = await callers.Admin.PostAsJsonAsync("/api/v1/enrolments", new { machine = name, kind = "intermittent" }, Token);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString();
        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync("/api/v1/ingest/enrol", new { code, machine = name }, Token);
        using var machine = _factory.CreateKeyClient((await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("key").GetString()!);
        (await machine.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version, pendingLines = 0, canUpdate }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return (callers.Admin, name);
    }

    private static async Task<string?> UpdateToAsync(HttpClient admin, string name) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token)).EnumerateArray().Single(item => item.GetProperty("machine").GetString() == name).GetProperty("updateTo").GetString();

    [Theory]
    [InlineData("1.3.0", true, "1.4.0")]
    [InlineData("0.0.0-dev.12", true, "1.4.0")]
    [InlineData("1.4.0", true, null)]
    [InlineData("1.5.0", true, null)]
    [InlineData("1.3.0", false, null)]
    [InlineData("not-a-version", true, null)]
    public async Task should_tell_only_a_machine_that_can_replace_itself_and_runs_an_older_version_which_one_is_there(string version, bool canUpdate, string? expected)
    {
        var (admin, name) = await MachineAsync(version, canUpdate);

        (await UpdateToAsync(admin, name)).Should().Be(expected);
    }
}
