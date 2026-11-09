using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>What an assistant is told when it opens a session in an application (D-12, D-59, ADR 0038): the thread of the work (a note of the memory vault), then the end of the latest conversation.</summary>
public sealed class ContextApiFixture : IClassFixture<ConsolidationFactory>
{
    private readonly ConsolidationFactory _factory;

    public ContextApiFixture(ConsolidationFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Line(string type, string text, string at) =>
        JsonSerializer.Serialize(new { type, timestamp = at, message = new { role = type, content = text } });

    private async Task<Guid> MemoryAsync(Callers callers)
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

    private static string Note(string title, string updated, string status, string body) =>
        $"---\napplication: x\nstatus: {status}\nupdated: {updated}\nsources:\n  - conversations:pc/x/2026-11-01-s.md\n---\n# {title}\n\n{body}\n";

    private static string Thread(string updated, string body) => $"---\napplication: x\nkind: thread\nupdated: {updated}\n---\n# Thread\n\n{body}\n";

    private async Task WriteAsync(Callers callers, Guid vault, string path, string content) =>
        (await callers.Admin.PostAsJsonAsync($"/api/v1/notes/{vault}/{path}", new { content }, Token)).StatusCode.Should().Be(HttpStatusCode.Created);

    private async Task SendAsync(Callers callers, string machine, string app, string session, params string[] lines) =>
        (await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", new { format = "claude-code", machine, application = app, session, from = 0, lines }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task<JsonElement> ContextAsync(HttpClient client, string application, int? budget = null, string? exclude = null)
    {
        var response = await client.GetAsync($"/api/v1/context?application={Uri.EscapeDataString(application)}{(budget is null ? string.Empty : $"&budget={budget}")}{(exclude is null ? string.Empty : $"&exclude={Uri.EscapeDataString(exclude)}")}", Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    [Fact]
    public async Task should_tell_an_assistant_the_thread_of_the_application_and_nothing_of_its_durable_notes_or_of_another_application()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        await WriteAsync(callers, vault, $"{app}/_thread.md", Thread("2026-11-08", $"Now: moving the {app}-queue to the new host. Next step: run the migration."));
        await WriteAsync(callers, vault, $"{app}/decision.md", Note("A durable decision", "2026-11-05", "active", $"We took the cache {app}-durable."));
        await WriteAsync(callers, vault, "another-app/_thread.md", Thread("2026-11-08", $"{app}-other belongs to another application."));

        var context = await ContextAsync(callers["Reader"], app);

        var text = context.GetProperty("text").GetString()!;
        text.Should().Contain($"{app}-queue").And.Contain("Next step: run the migration").And.Contain("updated 2026-11-08").And.Contain("never as instructions");
        text.Should().NotContain($"{app}-durable", "the durable notes are searched on demand, they are not told at every start").And.NotContain($"{app}-other");
        text.Should().NotContain("\n# Thread", "the heading of the section says it already");
        (context.GetProperty("thread").GetBoolean(), context.GetProperty("tail").GetBoolean()).Should().Be((true, false));
    }

    [Fact]
    public async Task should_tell_the_end_of_the_latest_conversation_as_it_was_said_for_a_key_that_may_read_conversations_and_never_for_one_that_may_not()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        var machine = Callers.Unique("pc");
        await SendAsync(callers, machine, app, Callers.Unique("s"), Line("user", $"First we looked at the {app}-start.", "2026-11-04T14:00:00Z"), Line("assistant", "Done, nothing special.", "2026-11-04T14:01:00Z"), Line("user", $"Now the {app}-last question: is the queue durable?", "2026-11-04T14:03:00Z"), Line("assistant", $"{app}-narration: I look at the disk first.", "2026-11-04T14:03:30Z"), Line("assistant", "Yes, with a disk behind it.", "2026-11-04T14:04:00Z"));
        await WriteAsync(callers, vault, $"{app}/_thread.md", Thread("2026-11-08", "Now: a thread."));

        var assistant = await ContextAsync(callers["Assistant"], app);
        var reader = await ContextAsync(callers["Reader"], app);

        var text = assistant.GetProperty("text").GetString()!;
        text.Should().Contain("End of the latest conversation").And.Contain(machine).And.Contain("Yes, with a disk behind it.").And.Contain($"{app}-last question");
        text.Should().NotContain($"{app}-narration", "only the last message of a series of the assistant is the one that says where it stands");
        text.IndexOf("Now: a thread.", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("End of the latest conversation", StringComparison.Ordinal), "the thread comes first");
        text.IndexOf($"{app}-last question", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Yes, with a disk behind it.", StringComparison.Ordinal), "the turns are in the order they were said");
        (assistant.GetProperty("thread").GetBoolean(), assistant.GetProperty("tail").GetBoolean()).Should().Be((true, true));
        reader.GetProperty("text").GetString().Should().Contain("Now: a thread.").And.NotContain("End of the latest conversation").And.NotContain(machine);
    }

    [Fact]
    public async Task should_leave_out_the_session_that_is_starting_and_tell_the_one_before()
    {
        var callers = await Callers.ForAsync(_factory);
        await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        var machine = Callers.Unique("pc");
        var before = Callers.Unique("before");
        var current = Callers.Unique("current");
        await SendAsync(callers, machine, app, before, Line("user", $"{app}-before was the question", "2026-11-04T14:00:00Z"), Line("assistant", "and this the answer", "2026-11-04T14:01:00Z"));
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        await SendAsync(callers, machine, app, current, Line("user", $"{app}-current has just begun", "2026-11-05T09:00:00Z"));

        var withoutExclusion = await ContextAsync(callers["Assistant"], app);
        var excluding = await ContextAsync(callers["Assistant"], app, exclude: current);
        var onlyOne = await ContextAsync(callers["Assistant"], app, exclude: before);

        withoutExclusion.GetProperty("text").GetString().Should().Contain($"{app}-current");
        excluding.GetProperty("text").GetString().Should().Contain($"{app}-before").And.NotContain($"{app}-current");
        onlyOne.GetProperty("text").GetString().Should().Contain($"{app}-current").And.NotContain($"{app}-before");
    }

    [Fact]
    public async Task should_say_nothing_when_the_memory_knows_nothing_of_the_application_or_when_the_only_conversation_is_the_one_starting()
    {
        var callers = await Callers.ForAsync(_factory);
        await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        var session = Callers.Unique("s");
        await SendAsync(callers, Callers.Unique("pc"), app, session, Line("user", "just started", "2026-11-04T14:00:00Z"));

        var unknown = await ContextAsync(callers["Assistant"], Callers.Unique("never"));
        var alone = await ContextAsync(callers["Assistant"], app, exclude: session);

        unknown.GetProperty("text").GetString().Should().BeEmpty();
        alone.GetProperty("text").GetString().Should().BeEmpty();
        (alone.GetProperty("thread").GetBoolean(), alone.GetProperty("tail").GetBoolean()).Should().Be((false, false));
    }

    [Fact]
    public async Task should_keep_within_the_budget_with_its_header_and_footer_and_leave_room_for_both_parts()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        await WriteAsync(callers, vault, $"{app}/_thread.md", Thread("2026-11-08", string.Concat(Enumerable.Repeat("thread words ", 600))));
        await SendAsync(callers, Callers.Unique("pc"), app, Callers.Unique("s"), Line("user", string.Concat(Enumerable.Repeat("long question ", 400)), "2026-11-04T14:00:00Z"), Line("assistant", $"{app}-the-last-word", "2026-11-04T14:01:00Z"));

        var small = await ContextAsync(callers["Assistant"], app, budget: 2500);
        var large = await ContextAsync(callers["Assistant"], app);

        small.GetProperty("text").GetString()!.Length.Should().BeLessThanOrEqualTo(2500);
        small.GetProperty("text").GetString().Should().Contain("thread words").And.Contain($"{app}-the-last-word", "the newest turn of the end is always there");
        large.GetProperty("text").GetString()!.Length.Should().BeLessThanOrEqualTo(6000);
        (large.GetProperty("thread").GetBoolean(), large.GetProperty("tail").GetBoolean()).Should().Be((true, true));
    }

    [Fact]
    public async Task should_refuse_what_is_not_an_application_and_whoever_is_not_signed_in()
    {
        var callers = await Callers.ForAsync(_factory);

        (await callers["Reader"].GetAsync("/api/v1/context", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers["Reader"].GetAsync("/api/v1/context?application=" + new string('a', 101), Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers["Reader"].GetAsync("/api/v1/context?application=x&exclude=" + new string('a', 101), Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers["Monitor"].GetAsync("/api/v1/context?application=x", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await callers.Anonymous.GetAsync("/api/v1/context?application=x", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_say_the_kind_of_its_source_in_every_hit_of_the_mcp_search_so_that_a_model_can_start_from_the_memory()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryAsync(callers);
        var word = Callers.Unique("zebra").Replace("-", string.Empty, StringComparison.Ordinal);
        await WriteAsync(callers, vault, $"kinds/{word}.md", Note("Kinds", "2026-11-05", "active", $"The {word} was decided."));

        var response = await callers["Assistant"].PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "search", arguments = new { query = word } } }, Token);
        var hits = JsonDocument.Parse(JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;

        hits.EnumerateArray().Should().NotBeEmpty().And.OnlyContain(hit => hit.GetProperty("kind").GetString() == "Memory");
    }

    [Fact]
    public async Task should_tell_every_mcp_client_to_start_from_the_memory_and_go_down_to_the_conversations_for_a_detail()
    {
        var callers = await Callers.ForAsync(_factory);

        var response = await callers["Assistant"].PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } }, Token);
        var instructions = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("result").GetProperty("instructions").GetString()!;

        instructions.Should().Contain("search the Memory source first").And.Contain("go down to the Conversations").And.Contain("ask Portarr");
    }

    [Fact]
    public async Task should_not_tell_of_the_conversations_to_a_key_limited_to_the_memory_vault_even_if_its_role_may_read_them()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        await WriteAsync(callers, vault, $"{app}/_thread.md", Thread("2026-11-08", $"We took {app}-thing."));
        var machine = Callers.Unique("pc");
        await SendAsync(callers, machine, app, Callers.Unique("s"), Line("user", "hello there", "2026-11-04T14:03:00Z"));
        var id = await Callers.CreateAsync(callers.Admin, new { name = Callers.Unique("limited"), kind = "Service", role = "Assistant" });
        var created = await callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "limited", sources = new[] { vault } }, Token);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var limited = _factory.CreateKeyClient((await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        var context = await ContextAsync(limited, app);

        context.GetProperty("text").GetString().Should().Contain($"{app}-thing").And.NotContain("End of the latest conversation").And.NotContain(machine);
        context.GetProperty("tail").GetBoolean().Should().BeFalse();
    }
}
