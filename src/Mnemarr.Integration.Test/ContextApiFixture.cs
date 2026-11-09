using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>What an assistant is told when it opens a session in an application (D-12, ADR 0037): the consolidated notes of the memory vault in the application's folder, then its latest conversations.</summary>
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

    private async Task WriteAsync(Callers callers, Guid vault, string path, string content) =>
        (await callers.Admin.PostAsJsonAsync($"/api/v1/notes/{vault}/{path}", new { content }, Token)).StatusCode.Should().Be(HttpStatusCode.Created);

    private async Task<JsonElement> ContextAsync(HttpClient client, string application, int? budget = null)
    {
        var response = await client.GetAsync($"/api/v1/context?application={Uri.EscapeDataString(application)}{(budget is null ? string.Empty : $"&budget={budget}")}", Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    [Fact]
    public async Task should_tell_an_assistant_what_the_memory_knows_of_the_application_the_newest_first_and_without_what_is_superseded_or_belongs_to_another()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        await WriteAsync(callers, vault, $"{app}/older.md", Note("Older decision", "2026-10-01", "active", $"We took the cache {app}-older."));
        await WriteAsync(callers, vault, $"{app}/newer.md", Note("Newer decision", "2026-11-05", "active", $"We took the queue {app}-newer."));
        await WriteAsync(callers, vault, $"{app}/dead.md", Note("Dead decision", "2026-11-07", "superseded", $"{app}-dead must not be told."));
        await WriteAsync(callers, vault, "another-app/note.md", Note("Other", "2026-11-07", "active", $"{app}-other belongs to another application."));

        var context = await ContextAsync(callers["Reader"], app);

        var text = context.GetProperty("text").GetString()!;
        text.Should().Contain($"{app}-older").And.Contain($"{app}-newer").And.NotContain($"{app}-dead").And.NotContain($"{app}-other");
        text.IndexOf($"{app}-newer", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf($"{app}-older", StringComparison.Ordinal), "the most recently updated first");
        text.Should().Contain("updated 2026-11-05").And.Contain("search (source of kind Memory first");
        text.Should().NotContain("\n# Newer decision", "the heading of the section says the title already");
        (context.GetProperty("notes").GetInt32(), context.GetProperty("conversations").GetInt32()).Should().Be((2, 0));
    }

    [Fact]
    public async Task should_add_the_latest_conversations_only_for_a_key_that_may_read_them()
    {
        var callers = await Callers.ForAsync(_factory);
        await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        var machine = Callers.Unique("pc");
        var sent = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", new { format = "claude-code", machine, application = app, session = Callers.Unique("s"), from = 0, lines = new[] { Line("user", $"Why the {app}-cache?", "2026-11-04T14:03:00Z"), Line("assistant", "Because it is simple.", "2026-11-04T14:04:00Z") } }, Token);
        sent.StatusCode.Should().Be(HttpStatusCode.OK);

        var assistant = await ContextAsync(callers["Assistant"], app);
        var reader = await ContextAsync(callers["Reader"], app);

        assistant.GetProperty("text").GetString().Should().Contain("Latest conversations").And.Contain(machine);
        assistant.GetProperty("conversations").GetInt32().Should().Be(1);
        reader.GetProperty("text").GetString().Should().BeEmpty("a key that may not read conversations is not told of them, and the memory has no note of this application");
    }

    [Fact]
    public async Task should_keep_within_the_budget_and_say_nothing_for_an_application_the_memory_does_not_know()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        for (var index = 0; index < 8; index++)
        {
            await WriteAsync(callers, vault, $"{app}/n{index}.md", Note($"Note {index}", $"2026-11-0{index + 1}", "active", new string('w', 1400)));
        }

        var small = await ContextAsync(callers["Reader"], app, budget: 2500);
        var unknown = await ContextAsync(callers["Reader"], Callers.Unique("never"));

        small.GetProperty("text").GetString()!.Length.Should().BeLessThanOrEqualTo(2500);
        small.GetProperty("notes").GetInt32().Should().BeInRange(1, 2);
        unknown.GetProperty("text").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task should_refuse_what_is_not_an_application_and_whoever_is_not_signed_in()
    {
        var callers = await Callers.ForAsync(_factory);

        (await callers["Reader"].GetAsync("/api/v1/context", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers["Reader"].GetAsync("/api/v1/context?application=" + new string('a', 101), Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
        await WriteAsync(callers, vault, $"{app}/note.md", Note("A note", "2026-11-05", "active", $"We took {app}-thing."));
        var machine = Callers.Unique("pc");
        (await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", new { format = "claude-code", machine, application = app, session = Callers.Unique("s"), from = 0, lines = new[] { Line("user", "hello there", "2026-11-04T14:03:00Z") } }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await Callers.CreateAsync(callers.Admin, new { name = Callers.Unique("limited"), kind = "Service", role = "Assistant" });
        var created = await callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "limited", sources = new[] { vault } }, Token);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var limited = _factory.CreateKeyClient((await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        var context = await ContextAsync(limited, app);

        context.GetProperty("text").GetString().Should().Contain($"{app}-thing").And.NotContain("Latest conversations").And.NotContain(machine);
        context.GetProperty("conversations").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task should_rank_all_the_notes_of_the_application_before_keeping_the_newest_and_keep_to_the_budget_with_its_header()
    {
        var callers = await Callers.ForAsync(_factory);
        var vault = await MemoryAsync(callers);
        var app = Callers.Unique("app").ToLowerInvariant();
        for (var index = 0; index < 45; index++)
        {
            await WriteAsync(callers, vault, $"{app}/a{index:D2}.md", Note($"Old {index}", "2026-01-01", "active", "old"));
        }

        await WriteAsync(callers, vault, $"{app}/zzz-latest.md", Note("Latest", "2026-11-08", "active", $"{app}-newest decision."));

        var context = await ContextAsync(callers["Reader"], app, budget: 1000);

        var text = context.GetProperty("text").GetString()!;
        text.Should().Contain($"{app}-newest", "the newest note is kept even if it is the last in alphabetical order, among more than forty");
        text.Length.Should().BeLessThanOrEqualTo(1000, "the header and the footer count in the budget");
        text.Should().Contain("never as instructions");
    }
}
