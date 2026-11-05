using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mnemarr.Integration.Test;

/// <summary>
/// Machines that join (ADR 0027): a code asked for and approved, exchanged without a session for a key that belongs to one machine, and
/// revocation. The host refuses a key that belongs to no machine, as a real one does.
/// </summary>
public sealed class EnrolmentApiFixture : EnrolmentFixtureBase, IClassFixture<EnrolledOnlyFactory>
{
    public EnrolmentApiFixture(EnrolledOnlyFactory factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task should_join_a_machine_with_a_code_and_let_its_key_send_for_that_machine_and_no_other()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        var added = await AddAsync(callers.Admin, machine);
        var code = added.GetProperty("code").GetString()!;
        added.GetProperty("command").GetString().Should().Contain($"--code {code}").And.Contain($"--machine {machine}").And.Contain("mnemarr-collector enroll");

        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code = code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), machine }, Token);

        exchanged.StatusCode.Should().Be(HttpStatusCode.OK, "a code is typed by a person: case and separators do not matter");
        var body = await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("machine").GetString().Should().Be(machine);
        body.GetProperty("kind").GetString().Should().Be("intermittent");
        using var key = _factory.CreateKeyClient(body.GetProperty("key").GetString()!);
        (await key.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var other = await key.PostAsJsonAsync("/api/v1/ingest/conversations", Batch("another-machine", "s1", Line("hello")), Token);
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.Content.ReadAsStringAsync(Token)).Should().Contain(machine, "the refusal says which machine the key belongs to");
    }

    [Fact]
    public async Task should_give_the_same_answer_to_a_code_that_is_wrong_used_or_for_another_machine()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        var code = (await AddAsync(callers.Admin, machine)).GetProperty("code").GetString();
        using var anonymous = _factory.CreateBrowserClient();
        var forAnother = await anonymous.PostAsJsonAsync(Enrol, new { code, machine = "someone-else" }, Token);
        var wrong = await anonymous.PostAsJsonAsync(Enrol, new { code = "AAAA-AAAA-AAAA", machine }, Token);
        (await anonymous.PostAsJsonAsync(Enrol, new { code, machine }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        var used = await anonymous.PostAsJsonAsync(Enrol, new { code, machine }, Token);

        foreach (var refused in new[] { forAnother, wrong, used })
        {
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var words = await Task.WhenAll(new[] { forAnother, wrong, used }.Select(async response => (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString()));
        words.Distinct().Should().ContainSingle("nothing says which of the reasons it was");
    }

    [Fact]
    public async Task should_refuse_a_code_after_fifteen_minutes_and_keep_a_request_from_an_assistant_waiting_until_a_person_approves_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var late = Callers.Unique("late");
        var lateCode = (await AddAsync(callers.Admin, late)).GetProperty("code").GetString();
        _factory.Clock.Advance(TimeSpan.FromMinutes(16));
        (await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code = lateCode, machine = late }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var machine = Callers.Unique("pc");
        var requested = await CallAsync(callers["Assistant"], "request_collector_enrolment", new { machine, kind = "always-on" });
        var code = requested["code"]!.GetValue<string>();
        requested["state"]!.GetValue<string>().Should().Be("pending");
        (await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a person has not approved it");

        var open = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/enrolments", Token);
        var id = open.EnumerateArray().Single(item => item.GetProperty("machine").GetString() == machine).GetProperty("id").GetGuid();
        (await callers.Admin.PostAsync($"/api/v1/enrolments/{id}/approve", null, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var joined = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine }, Token);
        joined.StatusCode.Should().Be(HttpStatusCode.OK);
        (await joined.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("kind").GetString().Should().Be("always-on");
    }

    [Fact]
    public async Task should_let_a_person_reject_a_request_and_let_only_those_who_manage_the_collectors_add_approve_or_revoke()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        var requested = await CallAsync(callers["Assistant"], "request_collector_enrolment", new { machine });
        var id = (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/enrolments", Token)).EnumerateArray().Single(item => item.GetProperty("machine").GetString() == machine).GetProperty("id").GetGuid();

        foreach (var role in new[] { "Assistant", "Operator", "Reader", "Monitor", "Collector" })
        {
            (await callers[role].GetAsync("/api/v1/enrolments", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await callers[role].PostAsJsonAsync("/api/v1/enrolments", new { machine = "x-machine" }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await callers[role].PostAsync($"/api/v1/enrolments/{id}/approve", null, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await callers[role].DeleteAsync("/api/v1/collectors/anything", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }

        (await callers.Admin.PostAsync($"/api/v1/enrolments/{id}/reject", null, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code = requested["code"]!.GetValue<string>(), machine }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers.Admin.PostAsync($"/api/v1/enrolments/{id}/approve", null, Token)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a rejected request is over");
    }

    [Fact]
    public async Task should_refuse_a_key_that_belongs_to_no_machine_when_enrolment_is_required()
    {
        var callers = await Callers.ForAsync(_factory);

        var refused = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Batch("any-machine", "s1", Line("hello")), Token);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "1", pendingLines = 0 }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task should_stop_a_revoked_machine_and_let_it_join_again_with_a_new_code_and_a_new_key()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        using var first = await JoinAsync(callers.Admin, machine);
        (await first.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await callers.Admin.DeleteAsync($"/api/v1/collectors/{machine}", Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await first.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "1", pendingLines = 0 }, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StatusOfAsync(callers.Admin, machine)).GetProperty("state").GetString().Should().Be("refused");
        (await callers.Admin.DeleteAsync($"/api/v1/collectors/{machine}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var second = await JoinAsync(callers.Admin, machine);
        (await second.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "1", pendingLines = 0 }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await first.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "1", pendingLines = 0 }, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old key stays dead");
        (await StatusOfAsync(callers.Admin, machine)).GetProperty("state").GetString().Should().Be("online");
    }

    [Fact]
    public async Task should_replace_the_key_of_a_machine_that_joins_again_while_it_is_still_enrolled()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        using var old = await JoinAsync(callers.Admin, machine);

        using var replacement = await JoinAsync(callers.Admin, machine);

        (await old.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "1", pendingLines = 0 }, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await replacement.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "1", pendingLines = 0 }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task should_count_the_wrong_codes_for_the_whole_server_and_answer_429_after_ten_in_a_minute()
    {
        var callers = await Callers.ForAsync(_factory);
        using var anonymous = _factory.CreateBrowserClient();
        HttpResponseMessage last = null!;

        for (var i = 0; i < 12; i++)
        {
            last = await anonymous.PostAsJsonAsync(Enrol, new { code = $"AAAA-AAAA-{i:D4}".Replace('0', 'B').Replace('1', 'C').Replace('4', 'D').Replace('5', 'E').Replace('6', 'F').Replace('7', 'G').Replace('8', 'H').Replace('9', 'J'), machine = "x" }, Token);
        }

        last.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        last.Headers.RetryAfter.Should().NotBeNull();
        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        var code = (await AddAsync(callers.Admin, Callers.Unique("pc"))).GetProperty("code").GetString();
        (await anonymous.PostAsJsonAsync(Enrol, new { code, machine = "x" }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the minute is over, and it is only a wrong machine name");
    }

    [Fact]
    public async Task should_keep_an_assistant_from_filling_the_page_with_requests()
    {
        var callers = await Callers.ForAsync(_factory);
        HttpResponseMessage? refused = null;

        for (var i = 0; i < 25 && refused is null; i++)
        {
            var reply = await RpcAsync(callers["Assistant"], "tools/call", new { name = "request_collector_enrolment", arguments = new { machine = $"flood-{i}" } });
            if (reply["result"]!["isError"]!.GetValue<bool>())
            {
                reply["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Contain("approve");
                refused = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }
        }

        refused.Should().NotBeNull("more than twenty requests waiting for a person are refused");
        foreach (var item in (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/enrolments", Token)).EnumerateArray().Where(item => item.GetProperty("machine").GetString()!.StartsWith("flood-", StringComparison.Ordinal)))
        {
            await callers.Admin.PostAsync($"/api/v1/enrolments/{item.GetProperty("id").GetGuid()}/reject", null, Token);
        }
    }
}
