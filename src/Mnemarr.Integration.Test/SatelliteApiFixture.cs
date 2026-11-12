using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>
/// What a satellite that is not a command-line tool can do through the API (ADR 0041): send a conversation in the neutral format and be found, ask a person to sign in again with a link, and
/// have its conversations left out of the notes of the memory unless a person says otherwise.
/// </summary>
public sealed class SatelliteApiFixture : IClassFixture<MnemarrFactory>
{
    private readonly MnemarrFactory _factory;

    public SatelliteApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(Callers Callers, HttpClient Machine, string Name)> SatelliteAsync()
    {
        var callers = await Callers.ForAsync(_factory);
        var name = Callers.Unique("gemini");
        var created = await callers.Admin.PostAsJsonAsync("/api/v1/enrolments", new { machine = name, kind = "always-on" }, Token);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString();
        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync("/api/v1/ingest/enrol", new { code, machine = name }, Token);
        return (callers, _factory.CreateKeyClient((await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("key").GetString()!), name);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient admin, string name) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token)).EnumerateArray().Single(item => item.GetProperty("machine").GetString() == name);

    private static Task<HttpResponseMessage> BeatAsync(HttpClient machine, object? attention) =>
        machine.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.3.0", pendingLines = 0, attention }, Token);

    [Fact]
    public async Task should_take_a_conversation_in_chat_turns_and_find_it_by_its_words_with_the_title_of_its_sender()
    {
        var (callers, machine, name) = await SatelliteAsync();
        var word = "lighthouse" + Guid.NewGuid().ToString("N")[..8];
        var lines = new[]
        {
            JsonSerializer.Serialize(new { v = 1, meta = new { title = $"About the {word}", source = "web", id = "c1" } }),
            JsonSerializer.Serialize(new { v = 1, role = "user", at = "2026-11-12T08:03:12Z", text = $"What is a {word}?" }),
            JsonSerializer.Serialize(new { v = 1, role = "assistant", at = (string?)null, text = "A tower with a light." }),
        };

        var sent = await machine.PostAsJsonAsync("/api/v1/ingest/conversations", new { format = "chat-turns", machine = name, application = "gemini", session = "gemini-c1", from = 0, lines }, Token);

        sent.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sent.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("next").GetInt64().Should().Be(3);
        var found = await callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/search?q={word}&topic={name}", Token);
        found.EnumerateArray().Should().NotBeEmpty().And.OnlyContain(hit => hit.GetProperty("topic").GetString() == name);
        var positions = await machine.GetFromJsonAsync<JsonElement>($"/api/v1/ingest/positions?machine={name}", Token);
        positions.EnumerateArray().Single().GetProperty("lines").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task should_show_the_request_of_a_satellite_as_failing_with_the_sentence_of_mnemarr_and_end_it_with_the_next_heartbeat()
    {
        var (callers, machine, name) = await SatelliteAsync();

        (await BeatAsync(machine, new { code = "needs-login", url = "https://geminarr.homelab.example/connection" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var asking = await StatusAsync(callers.Admin, name);
        asking.GetProperty("state").GetString().Should().Be("failing");
        var attention = asking.GetProperty("attention");
        attention.GetProperty("code").GetString().Should().Be("needs-login");
        attention.GetProperty("sentence").GetString().Should().Contain("sign in");
        attention.GetProperty("url").GetString().Should().Be("https://geminarr.homelab.example/connection");
        attention.GetProperty("since").ValueKind.Should().Be(JsonValueKind.String);

        (await BeatAsync(machine, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var calm = await StatusAsync(callers.Admin, name);
        calm.GetProperty("state").GetString().Should().Be("online");
        calm.GetProperty("attention").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("invented-code", "https://geminarr.homelab.example/connection", false, null)]
    [InlineData("needs-login", "javascript:alert(1)", true, null)]
    [InlineData("needs-login", "http://geminarr.homelab.example/connection", true, null)]
    [InlineData("needs-login", "https://user:secret@geminarr.homelab.example/", true, null)]
    public async Task should_ignore_a_code_that_is_not_on_the_list_and_drop_an_address_that_is_not_acceptable(string code, string url, bool shown, string? expectedUrl)
    {
        var (callers, machine, name) = await SatelliteAsync();

        (await BeatAsync(machine, new { code, url })).StatusCode.Should().Be(HttpStatusCode.NoContent, "an attention that is not understood never fails the heartbeat");

        var status = await StatusAsync(callers.Admin, name);
        (status.GetProperty("attention").ValueKind == JsonValueKind.Object).Should().Be(shown);
        if (shown)
        {
            status.GetProperty("attention").GetProperty("url").GetString().Should().Be(expectedUrl);
        }
    }

    [Fact]
    public async Task should_let_a_person_switch_the_notes_of_the_memory_on_and_off_and_say_what_is_in_force()
    {
        var (callers, machine, name) = await SatelliteAsync();
        await machine.PostAsJsonAsync("/api/v1/ingest/conversations", new { format = "chat-turns", machine = name, application = "gemini", session = "gemini-c2", from = 0, lines = new[] { JsonSerializer.Serialize(new { v = 1, meta = new { title = "", source = "web", id = "c2" } }), JsonSerializer.Serialize(new { v = 1, role = "user", at = (string?)null, text = "an idea" }) } }, Token);

        var byDefault = await StatusAsync(callers.Admin, name);
        (byDefault.GetProperty("consolidate").ValueKind, byDefault.GetProperty("consolidating").GetBoolean()).Should().Be((JsonValueKind.Null, false), "the conversations of a web assistant are not consolidated unless said");

        (await callers.Admin.PostAsJsonAsync($"/api/v1/collectors/{name}/consolidate", new { consolidate = true }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var on = await StatusAsync(callers.Admin, name);
        (on.GetProperty("consolidate").GetBoolean(), on.GetProperty("consolidating").GetBoolean()).Should().Be((true, true));

        (await callers.Admin.PostAsJsonAsync($"/api/v1/collectors/{name}/consolidate", new { consolidate = (bool?)null }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StatusAsync(callers.Admin, name)).GetProperty("consolidating").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task should_refuse_to_set_it_for_a_machine_that_does_not_exist_or_to_a_caller_who_may_not_manage_collectors()
    {
        var (callers, _, name) = await SatelliteAsync();

        (await callers.Admin.PostAsJsonAsync("/api/v1/collectors/nobody-at-all/consolidate", new { consolidate = true }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await callers["Reader"].PostAsJsonAsync($"/api/v1/collectors/{name}/consolidate", new { consolidate = true }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
