using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>A machine that asks to be linked, and a person who answers (ADR 0030).</summary>
public sealed class PairingApiFixture : EnrolmentFixtureBase, IClassFixture<EnrolledOnlyFactory>
{
    private const string Pair = "/api/v1/ingest/pair";
    private const string Poll = "/api/v1/ingest/pair/poll";

    public PairingApiFixture(EnrolledOnlyFactory factory)
        : base(factory)
    {
    }

    /// <summary>The requests are limited for a minute, for the whole server: each test begins in a minute of its own.</summary>
    private void NewMinute() => _factory.Clock.Advance(TimeSpan.FromSeconds(61));

    private async Task<JsonElement> StartAsync(string? hostname = "gaming-pc")
    {
        NewMinute();
        var response = await _factory.CreateBrowserClient().PostAsJsonAsync(Pair, new { hostname }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private async Task<JsonElement> PollAsync(string deviceCode, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _factory.CreateBrowserClient().PostAsJsonAsync(Poll, new { deviceCode }, Token);
        response.StatusCode.Should().Be(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>The code each request shows, as the person typed it: approving needs it.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> Codes = new();

    private static async Task<Guid> LookupAsync(HttpClient admin, string userCode)
    {
        var found = await admin.PostAsJsonAsync("/api/v1/pairings/lookup", new { code = userCode }, Token);
        found.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await found.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        Codes[id] = userCode;
        return id;
    }

    [Fact]
    public async Task should_link_a_machine_that_asked_when_a_person_approves_it_and_let_its_key_send_for_that_name_only()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        var started = await StartAsync("Gaming PC");
        var userCode = started.GetProperty("userCode").GetString()!;
        var deviceCode = started.GetProperty("deviceCode").GetString()!;
        started.GetProperty("verificationUrl").GetString().Should().EndWith($"/link?code={Uri.EscapeDataString(userCode)}");
        started.GetProperty("expiresInSeconds").GetInt32().Should().Be(600);
        started.GetProperty("intervalSeconds").GetInt32().Should().BeGreaterThan(0);
        (await PollAsync(deviceCode)).GetProperty("state").GetString().Should().Be("pending");

        var id = await LookupAsync(callers.Admin, userCode.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal));
        (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/pairings", Token)).EnumerateArray().Should().Contain(item => item.GetProperty("id").GetGuid() == id && item.GetProperty("hostname").GetString() == "Gaming PC");
        (await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = Codes[id], machine, kind = "always-on" }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        var answer = await PollAsync(deviceCode);
        answer.GetProperty("state").GetString().Should().Be("approved");
        answer.GetProperty("machine").GetString().Should().Be(machine);
        answer.GetProperty("kind").GetString().Should().Be("always-on");
        using var key = _factory.CreateKeyClient(answer.GetProperty("key").GetString()!);
        (await key.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await key.PostAsJsonAsync("/api/v1/ingest/conversations", Batch("another-machine", "s1", Line("hello")), Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StatusOfAsync(callers.Admin, machine)).GetProperty("kind").GetString().Should().Be("always-on");
        (await PollAsync(deviceCode, HttpStatusCode.BadRequest)).GetProperty("title").GetString().Should().NotBeNullOrEmpty("the key is shown once");
        (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/pairings", Token)).EnumerateArray().Should().NotContain(item => item.GetProperty("id").GetGuid() == id, "an answered request is not waiting any more");
    }

    [Fact]
    public async Task should_not_approve_a_request_without_the_code_the_computer_shows_or_with_the_code_of_another()
    {
        var callers = await Callers.ForAsync(_factory);
        var mine = await StartAsync("Gaming PC");
        var id = await LookupAsync(callers.Admin, mine.GetProperty("userCode").GetString()!);
        var other = await StartAsync("Another PC");
        var otherCode = other.GetProperty("userCode").GetString();

        var none = await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { machine = Callers.Unique("pc"), kind = "intermittent" }, Token);
        var wrong = await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = otherCode, machine = Callers.Unique("pc"), kind = "intermittent" }, Token);

        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a request is approved by its own code, not by its name or by another's code");
        (await PollAsync(mine.GetProperty("deviceCode").GetString()!)).GetProperty("state").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task should_tell_a_machine_that_a_person_refused_it_and_give_it_nothing()
    {
        var callers = await Callers.ForAsync(_factory);
        var started = await StartAsync();
        var id = await LookupAsync(callers.Admin, started.GetProperty("userCode").GetString()!);

        (await callers.Admin.PostAsync($"/api/v1/pairings/{id}/reject", null, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var answer = await PollAsync(started.GetProperty("deviceCode").GetString()!);
        answer.GetProperty("state").GetString().Should().Be("rejected");
        answer.TryGetProperty("key", out var key).Should().BeTrue();
        key.ValueKind.Should().Be(JsonValueKind.Null);
        (await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = Codes[id], machine = Callers.Unique("pc"), kind = "intermittent" }, Token)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a refused request cannot be approved afterwards");
    }

    [Fact]
    public async Task should_let_a_request_expire_after_ten_minutes_for_the_machine_and_for_the_person()
    {
        var callers = await Callers.ForAsync(_factory);
        var started = await StartAsync();
        var userCode = started.GetProperty("userCode").GetString()!;
        var id = await LookupAsync(callers.Admin, userCode);

        _factory.Clock.Advance(TimeSpan.FromMinutes(11));

        (await PollAsync(started.GetProperty("deviceCode").GetString()!)).GetProperty("state").GetString().Should().Be("expired");
        (await callers.Admin.PostAsJsonAsync("/api/v1/pairings/lookup", new { code = userCode }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = Codes[id], machine = Callers.Unique("pc"), kind = "intermittent" }, Token)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task should_let_only_those_who_manage_the_collectors_see_and_answer_requests()
    {
        var callers = await Callers.ForAsync(_factory);
        var started = await StartAsync();
        var userCode = started.GetProperty("userCode").GetString()!;
        var id = await LookupAsync(callers.Admin, userCode);

        foreach (var role in new[] { "Reader", "Operator", "Assistant" })
        {
            (await callers[role].GetAsync("/api/v1/pairings", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await callers[role].PostAsJsonAsync("/api/v1/pairings/lookup", new { code = userCode }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await callers[role].PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = Codes[id], machine = Callers.Unique("pc"), kind = "intermittent" }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await callers[role].PostAsync($"/api/v1/pairings/{id}/reject", null, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }

        (await callers.Anonymous.GetAsync("/api/v1/pairings", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PollAsync(started.GetProperty("deviceCode").GetString()!)).GetProperty("state").GetString().Should().Be("pending", "nothing was answered");
    }

    [Fact]
    public async Task should_refuse_a_name_that_is_not_one_and_a_kind_that_is_not_one_and_keep_the_request_waiting()
    {
        var callers = await Callers.ForAsync(_factory);
        var started = await StartAsync();
        var id = await LookupAsync(callers.Admin, started.GetProperty("userCode").GetString()!);

        (await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = Codes[id], machine = "a'; calc", kind = "intermittent" }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = Codes[id], machine = Callers.Unique("pc"), kind = "sometimes" }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await PollAsync(started.GetProperty("deviceCode").GetString()!)).GetProperty("state").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task should_replace_the_key_of_a_machine_of_that_name_when_a_person_approves_a_request_for_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        using var old = await JoinAsync(callers.Admin, machine);
        var started = await StartAsync();
        var id = await LookupAsync(callers.Admin, started.GetProperty("userCode").GetString()!);

        await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = Codes[id], machine, kind = "intermittent" }, Token);
        var answer = await PollAsync(started.GetProperty("deviceCode").GetString()!);

        (await old.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s1", Line("hello")), Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the key it had is replaced");
        using var fresh = _factory.CreateKeyClient(answer.GetProperty("key").GetString()!);
        (await fresh.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s1", Line("hello")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("DESKTOP-ABC_1", "DESKTOP-ABC_1")]
    [InlineData("  my pc \r\n## injected [[link]]", "my pc  injected link")]
    [InlineData("", "unnamed computer")]
    [InlineData(null, "unnamed computer")]
    public async Task should_show_the_name_a_machine_gives_itself_as_plain_short_text(string? hostname, string expected)
    {
        var callers = await Callers.ForAsync(_factory);
        var started = await StartAsync(hostname);

        var id = await LookupAsync(callers.Admin, started.GetProperty("userCode").GetString()!);

        (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/pairings", Token)).EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id).GetProperty("hostname").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task should_count_the_wrong_secrets_for_the_whole_server_and_give_the_same_answer_to_each()
    {
        var callers = await Callers.ForAsync(_factory);
        NewMinute();
        using var anonymous = _factory.CreateBrowserClient();
        HttpResponseMessage last = null!;

        for (var i = 0; i < 12; i++)
        {
            last = await anonymous.PostAsJsonAsync(Poll, new { deviceCode = $"dvc_not-a-secret-{i}" }, Token);
        }

        last.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        var started = await StartAsync();
        (await PollAsync(started.GetProperty("deviceCode").GetString()!)).GetProperty("state").GetString().Should().Be("pending", "the minute is over");
        (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/pairings", Token)).ValueKind.Should().Be(JsonValueKind.Array);
    }
}
