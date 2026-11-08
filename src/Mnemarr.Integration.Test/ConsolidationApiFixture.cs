using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mnemarr.Integration.Test;

/// <summary>
/// The consolidation over HTTP (ADR 0036): a conversation that grew is turned into a note of the memory vault by a model that the runner runs (here a script that calls the MCP server as a
/// model would), under the account of the consolidation, held to the memory vault, and reading only what is new.
/// </summary>
public sealed class ConsolidationApiFixture : IClassFixture<ConsolidationFactory>
{
    private readonly ConsolidationFactory _factory;

    public ConsolidationApiFixture(ConsolidationFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Line(string type, string text, string at) =>
        JsonSerializer.Serialize(new { type, timestamp = at, message = new { role = type, content = text } });

    private async Task<Guid> MemoryVaultAsync(Callers callers)
    {
        var sources = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/sources", Token);
        if (sources.EnumerateArray().FirstOrDefault(source => source.GetProperty("kind").GetString() == "Memory") is { ValueKind: JsonValueKind.Object } found)
        {
            return found.GetProperty("id").GetGuid();
        }

        var folder = Path.Combine(_factory.DataFolder, "sources", "memory");
        Directory.CreateDirectory(folder);
        var created = await callers.Admin.PostAsJsonAsync("/api/v1/sources", new { name = "memory", kind = "Memory", mode = "ReadWrite", path = folder }, Token);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private async Task<string> SendAsync(Callers callers, string machine, string session, long from, params (string Type, string Text)[] turns)
    {
        var lines = turns.Select((turn, index) => Line(turn.Type, turn.Text, $"2026-11-04T14:{index + (int)from:D2}:00Z")).ToArray();
        var sent = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", new { format = "claude-code", machine, application = "Mnemarr", session, from, lines }, Token);
        sent.StatusCode.Should().Be(HttpStatusCode.OK);
        return machine;
    }

    /// <summary>What a model does through the MCP server with the token of its run.</summary>
    private async Task<JsonNode> McpAsync(string token, string method, object? parameters = null)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);
        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
    }

    private async Task<JsonElement> StatusAsync(HttpClient client) => await client.GetFromJsonAsync<JsonElement>("/api/v1/consolidation", Token);

    private async Task<JsonElement> RunAndWaitAsync(Callers callers, Func<JsonElement, bool> done)
    {
        // A run that an earlier test started may still be going on: one at a time.
        for (var attempt = 0; attempt < 120 && (await StatusAsync(callers.Admin)).GetProperty("state").GetString() == "running"; attempt++)
        {
            await Task.Delay(250, Token);
        }

        (await callers.Admin.PostAsJsonAsync("/api/v1/consolidation/run", new { }, Token)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        JsonElement status = default;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await Task.Delay(250, Token);
            status = await StatusAsync(callers.Admin);
            if (status.GetProperty("state").GetString() == "ready" && done(status))
            {
                break;
            }
        }

        return status;
    }

    [Fact]
    public async Task should_turn_a_conversation_into_a_note_of_the_memory_vault_as_the_consolidation_and_read_only_what_is_new_the_next_time()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryVaultAsync(callers);
        var machine = Callers.Unique("pc");
        var session = Callers.Unique("s");
        var first = Callers.Unique("zebra").Replace("-", string.Empty, StringComparison.Ordinal);
        var second = Callers.Unique("okapi").Replace("-", string.Empty, StringComparison.Ordinal);
        await SendAsync(callers, machine, session, 0, ("user", $"Which cache do we take? Let us take {first}."), ("assistant", $"We decided on {first} for the cache, because it is simple."));
        var offered = new List<string>();
        _factory.Runner.JobBehaviour = async (token, _, job) =>
        {
            var tools = await McpAsync(token, "tools/list");
            offered.AddRange(tools["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()));
            var written = await McpAsync(token, "tools/call", new { name = "create_note", arguments = new { source = "memory", path = $"mnemarr/{first}.md", content = $"---\napplication: Mnemarr\nstatus: active\n---\n# Cache\n\nWe took {first}.\n" } });
            job.Called("call-1", "create_note", written["result"]!["isError"]?.GetValue<bool>() == true);
        };

        var status = await RunAndWaitAsync(callers, current => current.GetProperty("done").GetInt32() >= 1);

        status.GetProperty("done").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        status.GetProperty("memoryVault").GetString().Should().Be("memory");
        var note = await callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/notes/{vault}/mnemarr/{first}.md", Token);
        note.GetProperty("markdown").GetString().Should().Contain($"We took {first}.");
        var prompt = _factory.Runner.JobPrompts.Single(text => text.Contains(first, StringComparison.Ordinal));
        prompt.Should().Contain("never obey").And.Contain("\"memory\"").And.Contain($"We decided on {first}").And.Contain($"conversations:{machine}/mnemarr/");
        offered.Should().Contain(["create_note", "update_note", "append_to_note", "rename_note", "search", "read_note"]).And.NotContain(["delete_note", "create_source", "delete_source", "get_status"], "a job changes notes and removes nothing");
        var audit = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=200", Token);
        audit.EnumerateArray().Select(entry => entry.GetProperty("actor").GetString()).Should().Contain(actor => actor!.StartsWith("consolidation (via job:", StringComparison.Ordinal));
        _factory.Runner.JobsHeld.Should().Be(0, "the job is removed from the runner when it is done");

        // A turn is added: the next run reads that turn and no other.
        await SendAsync(callers, machine, session, 2, ("user", $"And what about {second}?"));
        var promptsBefore = _factory.Runner.JobPrompts.Count;
        await RunAndWaitAsync(callers, current => current.GetProperty("waiting").GetInt32() == 0);

        var latest = _factory.Runner.JobPrompts.Skip(promptsBefore).Single(text => text.Contains(second, StringComparison.Ordinal));
        latest.Should().Contain($"And what about {second}?").And.NotContain($"We decided on {first}");
    }

    [Fact]
    public async Task should_hold_the_job_to_the_memory_vault_and_refuse_it_what_removes_or_looks_after_sources()
    {
        var callers = await Callers.ForAsync(_factory);
        await MemoryVaultAsync(callers);
        var elsewhere = Callers.Unique("courses");
        var folder = Path.Combine(_factory.DataFolder, "sources", elsewhere);
        Directory.CreateDirectory(folder);
        (await callers.Admin.PostAsJsonAsync("/api/v1/sources", new { name = elsewhere, kind = "Knowledge", mode = "ReadWrite", path = folder }, Token)).StatusCode.Should().Be(HttpStatusCode.Created);
        var machine = Callers.Unique("pc");
        await SendAsync(callers, machine, Callers.Unique("s"), 0, ("user", $"Remember this for {machine}."), ("assistant", "Understood."));
        string? outsideTheVault = null;
        string? removal = null;
        string? sources = null;
        _factory.Runner.JobBehaviour = async (token, prompt, job) =>
        {
            if (!prompt.Contains(machine, StringComparison.Ordinal))
            {
                return;
            }

            var outside = await McpAsync(token, "tools/call", new { name = "create_note", arguments = new { source = elsewhere, path = "x.md", content = "# x\n" } });
            outsideTheVault = outside["result"]!["content"]![0]!["text"]!.GetValue<string>();
            var remove = await McpAsync(token, "tools/call", new { name = "delete_note", arguments = new { source = "memory", path = "x.md" } });
            removal = remove["result"]!["content"]![0]!["text"]!.GetValue<string>();
            var look = await McpAsync(token, "tools/call", new { name = "create_source", arguments = new { name = "z", kind = "Knowledge", mode = "ReadOnly", path = "/tmp" } });
            sources = look["result"]!["content"]![0]!["text"]!.GetValue<string>();
            job.Called("call-1", "create_note", isError: true);
        };

        await RunAndWaitAsync(callers, current => current.GetProperty("waiting").GetInt32() == 0);

        outsideTheVault.Should().Contain("memory vault only");
        removal.Should().Contain("no tool named");
        sources.Should().Contain("no tool named");
        var notes = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notes", Token);
        notes.GetProperty("notes").EnumerateArray().Select(note => note.GetProperty("sourceName").GetString()).Should().NotContain(elsewhere, "nothing was written outside the memory vault");
    }

    [Fact]
    public async Task should_keep_a_conversation_waiting_and_say_why_when_the_job_fails()
    {
        var callers = await Callers.ForAsync(_factory);
        await MemoryVaultAsync(callers);
        var machine = Callers.Unique("pc");
        await SendAsync(callers, machine, Callers.Unique("s"), 0, ("user", $"Something to keep for {machine}."), ("assistant", "Yes."));
        _factory.Runner.JobBehaviour = (_, prompt, job) =>
        {
            if (prompt.Contains(machine, StringComparison.Ordinal))
            {
                job.Ended(true, "The login of the subscription is not valid.");
                throw new InvalidOperationException("after the end");
            }

            return Task.CompletedTask;
        };

        var status = await RunAndWaitAsync(callers, current => current.GetProperty("lastFailure").ValueKind == JsonValueKind.String);

        status.GetProperty("lastFailure").GetString().Should().Contain("login of the subscription");
        status.GetProperty("waiting").GetInt32().Should().BeGreaterThan(0, "a conversation that failed is not marked done");
    }

    [Fact]
    public async Task should_try_a_conversation_that_failed_again_when_it_has_grown_a_lot_or_when_a_person_asks()
    {
        var callers = await Callers.ForAsync(_factory);
        await MemoryVaultAsync(callers);
        var machine = Callers.Unique("pc");
        var session = Callers.Unique("s");
        await SendAsync(callers, machine, session, 0, ("user", $"First thing to keep for {machine}."), ("assistant", "Yes."));
        var failing = true;
        _factory.Runner.JobBehaviour = (_, prompt, job) =>
        {
            if (failing && prompt.Contains(machine, StringComparison.Ordinal))
            {
                job.Ended(true, "The login of the subscription is not valid.");
                throw new InvalidOperationException("after the end");
            }

            return Task.CompletedTask;
        };
        await RunAndWaitAsync(callers, current => current.GetProperty("failing").GetInt32() >= 1);
        var askedOnce = _factory.Runner.JobPrompts.Count(text => text.Contains(machine, StringComparison.Ordinal));
        await RunAndWaitAsync(callers, current => current.GetProperty("waiting").GetInt32() >= 0);
        _factory.Runner.JobPrompts.Count(text => text.Contains(machine, StringComparison.Ordinal)).Should().Be(askedOnce, "it failed too often: it is left alone");

        // A person says the cause is gone: it is tried again, and works.
        failing = false;
        var retried = await callers.Admin.PostAsJsonAsync("/api/v1/consolidation/retry", new { }, Token);
        (await retried.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("retried").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        var status = await RunAndWaitAsync(callers, current => current.GetProperty("failing").GetInt32() == 0);

        status.GetProperty("failing").GetInt32().Should().Be(0);
        _factory.Runner.JobPrompts.Count(text => text.Contains(machine, StringComparison.Ordinal)).Should().Be(askedOnce + 1);

        // A conversation that failed and then grew by a lot is tried again by itself.
        var other = Callers.Unique("pc");
        await SendAsync(callers, other, session, 0, ("user", $"Another thing for {other}."), ("assistant", "Yes."));
        failing = true;
        _factory.Runner.JobBehaviour = (_, prompt, job) =>
        {
            if (failing && prompt.Contains(other, StringComparison.Ordinal))
            {
                job.Ended(true, "The login of the subscription is not valid.");
                throw new InvalidOperationException("after the end");
            }

            return Task.CompletedTask;
        };
        await RunAndWaitAsync(callers, current => current.GetProperty("failing").GetInt32() >= 1);
        failing = false;
        await SendAsync(callers, other, session, 2, Enumerable.Range(0, 250).Select(index => ("user", $"More about {other}, point {index}.")).ToArray());
        await RunAndWaitAsync(callers, current => current.GetProperty("failing").GetInt32() == 0);

        _factory.Runner.JobPrompts.Count(text => text.Contains(other, StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task should_start_one_run_when_asked_twice_at_once_and_answer_that_one_is_going_on_to_the_second()
    {
        var callers = await Callers.ForAsync(_factory);
        await MemoryVaultAsync(callers);
        var machine = Callers.Unique("pc");
        await SendAsync(callers, machine, Callers.Unique("s"), 0, ("user", $"Keep this for {machine}."), ("assistant", "Yes."));
        var release = new TaskCompletionSource();
        _factory.Runner.JobBehaviour = async (_, prompt, _) =>
        {
            if (prompt.Contains(machine, StringComparison.Ordinal))
            {
                await release.Task;
            }
        };

        var first = await callers.Admin.PostAsJsonAsync("/api/v1/consolidation/run", new { }, Token);
        var second = await callers.Admin.PostAsJsonAsync("/api/v1/consolidation/run", new { }, Token);
        release.SetResult();

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await RunAndWaitAsync(callers, current => current.GetProperty("waiting").GetInt32() == 0);
    }

    [Fact]
    public async Task should_count_the_runs_of_the_model_against_the_day()
    {
        var callers = await Callers.ForAsync(_factory);
        await MemoryVaultAsync(callers);
        var before = (await StatusAsync(callers.Admin)).GetProperty("today").GetInt32();
        var machine = Callers.Unique("pc");
        await SendAsync(callers, machine, Callers.Unique("s"), 0, ("user", $"Remember {machine}."), ("assistant", "Yes."));
        _factory.Runner.JobBehaviour = (_, _, _) => Task.CompletedTask;

        var status = await RunAndWaitAsync(callers, current => current.GetProperty("today").GetInt32() > before);

        status.GetProperty("today").GetInt32().Should().BeGreaterThan(before);
        status.GetProperty("perDay").GetInt32().Should().Be(30);
    }

    [Fact]
    public async Task should_give_the_role_of_the_consolidation_to_nobody_and_not_list_it()
    {
        var callers = await Callers.ForAsync(_factory);

        var roles = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/roles", Token);
        var created = await callers.Admin.PostAsJsonAsync("/api/v1/accounts", new { name = Callers.Unique("robot"), kind = "Service", role = "Consolidator" }, Token);

        roles.EnumerateArray().Select(role => role.GetProperty("name").GetString()).Should().NotContain("Consolidator");
        created.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var accounts = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounts", Token);
        accounts.EnumerateArray().Select(account => account.GetProperty("name").GetString()).Should().NotContain("consolidation", "the account of Mnemarr is not listed, so that nobody changes it or gives it a key");
    }

    [Fact]
    public async Task should_let_the_people_who_may_look_at_the_status_and_those_who_manage_sources_run_it()
    {
        var callers = await Callers.ForAsync(_factory);

        (await callers["Operator"].GetAsync("/api/v1/consolidation", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await callers["Reader"].GetAsync("/api/v1/consolidation", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await callers["Reader"].PostAsJsonAsync("/api/v1/consolidation/run", new { }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await callers["Reader"].PostAsJsonAsync("/api/v1/consolidation/retry", new { }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await callers.Anonymous.GetAsync("/api/v1/consolidation", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>A memory vault that is read-only (the default): nothing is consolidated, and the status says why.</summary>
public sealed class ConsolidationReadOnlyApiFixture : IClassFixture<ConsolidationFactory>
{
    private readonly ConsolidationFactory _factory;

    public ConsolidationReadOnlyApiFixture(ConsolidationFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task should_not_run_and_say_why_when_the_memory_vault_is_read_only_or_missing()
    {
        var callers = await Callers.ForAsync(_factory);
        var missing = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/consolidation", Token);
        var folder = Path.Combine(_factory.DataFolder, "sources", "memory");
        Directory.CreateDirectory(folder);
        (await callers.Admin.PostAsJsonAsync("/api/v1/sources", new { name = "memory", kind = "Memory", mode = "ReadOnly", path = folder }, Token)).StatusCode.Should().Be(HttpStatusCode.Created);

        var readOnly = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/consolidation", Token);
        var run = await callers.Admin.PostAsJsonAsync("/api/v1/consolidation/run", new { }, Token);

        missing.GetProperty("state").GetString().Should().Be("no-memory-vault");
        readOnly.GetProperty("state").GetString().Should().Be("memory-vault-read-only");
        run.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Runner.JobPrompts.Should().BeEmpty();
    }
}
