using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>
/// What a collector sends, over HTTP: taken with an ingest-only key, written as notes that an assistant finds at once through the same API and
/// MCP as any note, and read by nobody who holds only that key.
/// </summary>
public sealed class IngestApiFixture : IClassFixture<MnemarrFactory>
{
    private readonly MnemarrFactory _factory;

    public IngestApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Line(string type, string text, string at) =>
        JsonSerializer.Serialize(new { type, timestamp = at, message = new { role = type, content = text } });

    private static object Body(string machine, string session, long from, params string[] lines) =>
        new { format = "claude-code", machine, application = "Mnemarr", session, from, lines };

    [Fact]
    public async Task should_take_a_conversation_from_a_collector_and_let_an_assistant_find_it_through_search_and_mcp()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        var session = Callers.Unique("s");
        var word = Callers.Unique("zebra").Replace("-", string.Empty, StringComparison.Ordinal);

        var sent = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(machine, session, 0, Line("user", $"We decided on {word} for the cache.", "2026-11-04T14:03:00Z"), Line("assistant", "Noted.", "2026-11-04T14:04:00Z")), Token);

        sent.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sent.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("next").GetInt64().Should().Be(2);
        var found = await callers["Assistant"].GetFromJsonAsync<JsonElement>($"/api/v1/search?q={word}", Token);
        found.EnumerateArray().Select(hit => hit.GetProperty("path").GetString()).Distinct().Should().ContainSingle().Which.Should().StartWith($"{machine}/mnemarr/2026-11-04-");
        var mcp = await callers["Assistant"].PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "search", arguments = new { query = word } } }, Token);
        var mcpText = await mcp.Content.ReadAsStringAsync(Token);
        mcpText.Should().Contain($"{machine}/mnemarr/2026-11-04-");
        mcpText.Should().Contain("match", "the tool says how each hit matched");
    }

    [Fact]
    public async Task should_resume_a_collector_that_sends_again_or_skips_ahead_by_telling_it_the_position()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        var session = Callers.Unique("s");
        var first = Line("user", "one", "2026-11-04T14:03:00Z");
        await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(machine, session, 0, first), Token);

        var again = await (await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(machine, session, 0, first), Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var ahead = await (await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(machine, session, 9, first), Token)).Content.ReadFromJsonAsync<JsonElement>(Token);

        (again.GetProperty("next").GetInt64(), again.GetProperty("accepted").GetInt32()).Should().Be((1L, 0));
        (ahead.GetProperty("next").GetInt64(), ahead.GetProperty("accepted").GetInt32()).Should().Be((1L, 0), "a hole is not filled: the collector is told where to resume");
    }

    [Fact]
    public async Task should_let_only_the_ingest_role_send_and_the_ingest_role_read_nothing()
    {
        var callers = await Callers.ForAsync(_factory);
        var body = Body(Callers.Unique("pc"), Callers.Unique("s"), 0, Line("user", "x", "2026-11-04T14:03:00Z"));

        foreach (var role in new[] { "Reader", "Assistant", "Monitor", "Operator" })
        {
            (await callers[role].PostAsJsonAsync("/api/v1/ingest/conversations", body, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }

        (await callers.Anonymous.PostAsJsonAsync("/api/v1/ingest/conversations", body, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var url in new[] { "/api/v1/notes", "/api/v1/search?q=x", "/api/v1/collectors", "/api/v1/sources" })
        {
            (await callers["Collector"].GetAsync(url, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
        }
    }

    [Fact]
    public async Task should_refuse_a_batch_that_is_not_one_with_a_400_and_write_nothing()
    {
        var callers = await Callers.ForAsync(_factory);

        var badName = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body("../x", "s1", 0, "{}"), Token);
        var noLines = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body("pc", "s1", 0), Token);
        var noPosition = await callers["Collector"].PostAsync("/api/v1/ingest/conversations", new StringContent("{\"format\":\"claude-code\",\"machine\":\"pc\",\"application\":\"a\",\"session\":\"s1\",\"lines\":[\"{}\"]}", System.Text.Encoding.UTF8, "application/json"), Token);

        badName.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noLines.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noPosition.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_refuse_a_batch_with_too_many_lines_or_a_line_that_is_too_long_with_a_400_and_write_nothing()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("big");
        var tooMany = Enumerable.Repeat("{}", Mnemarr.Core.Conversations.ConversationIngestor.MaxLines + 1).ToArray();
        var tooLong = new string('x', Mnemarr.Core.Conversations.ConversationIngestor.MaxLineChars + 1);

        var many = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(machine, "s1", 0, tooMany), Token);
        var lineTooLong = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(machine, "s2", 0, tooLong), Token);

        many.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        lineTooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var machines = await callers["Admin"].GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token);
        machines.EnumerateArray().Select(item => item.GetProperty("machine").GetString()).Should().NotContain(machine, "a refused batch writes nothing");
    }

    [Fact]
    public async Task should_show_the_operator_which_machines_sent_something_and_when()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(machine, Callers.Unique("s"), 0, Line("user", "x", "2026-11-04T14:03:00Z")), Token);

        var machines = await callers["Admin"].GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token);

        machines.EnumerateArray().Should().Contain(item => item.GetProperty("machine").GetString() == machine && item.GetProperty("conversations").GetInt32() == 1);
    }

    [Fact]
    public async Task should_show_the_machines_only_to_those_who_may_read_the_conversations()
    {
        var callers = await Callers.ForAsync(_factory);

        foreach (var role in new[] { "Admin", "Assistant" })
        {
            (await callers[role].GetAsync("/api/v1/collectors", Token)).StatusCode.Should().Be(HttpStatusCode.OK, role);
        }

        foreach (var role in new[] { "Monitor", "Operator", "Reader", "Collector" })
        {
            (await callers[role].GetAsync("/api/v1/collectors", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
    }

    [Fact]
    public async Task should_show_the_conversations_as_a_source_that_nobody_can_write_in_by_hand()
    {
        var callers = await Callers.ForAsync(_factory);
        await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(Callers.Unique("pc"), Callers.Unique("s"), 0, Line("user", "hello there", "2026-11-04T14:03:00Z")), Token);

        var tree = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notes", Token);

        var source = tree.GetProperty("sources").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "Conversations");
        source.GetProperty("rights").GetProperty("canWrite").GetBoolean().Should().BeFalse();
        var declared = await callers["Operator"].PostAsJsonAsync("/api/v1/sources", new { name = "mine", kind = "Conversations", mode = "ReadOnly", path = Path.Combine(_factory.DataFolder, "sources") }, Token);
        declared.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await declared.Content.ReadAsStringAsync(Token)).Should().Contain("created by Mnemarr", "it is the rule of the kind that refuses it, not the path");
    }

    [Fact]
    public async Task should_show_what_was_said_to_the_assistants_only_to_the_roles_that_hold_conversations_read()
    {
        var callers = await Callers.ForAsync(_factory);
        var word = Callers.Unique("quokka").Replace("-", string.Empty, StringComparison.Ordinal);
        var sent = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", Body(Callers.Unique("pc"), Callers.Unique("s"), 0, Line("user", $"A private thought about {word}.", "2026-11-04T14:03:00Z")), Token);
        sent.EnsureSuccessStatusCode();
        var sourceId = (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notes", Token)).GetProperty("sources").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "Conversations").GetProperty("id").GetGuid();

        foreach (var role in new[] { "Reader", "Operator" })
        {
            var tree = await callers[role].GetFromJsonAsync<JsonElement>("/api/v1/notes", Token);
            tree.GetProperty("sources").EnumerateArray().Should().NotContain(item => item.GetProperty("kind").GetString() == "Conversations", role);
            tree.GetProperty("notes").EnumerateArray().Should().NotContain(item => item.GetProperty("sourceId").GetGuid() == sourceId, role);
            (await callers[role].GetFromJsonAsync<JsonElement>($"/api/v1/search?q={word}", Token)).GetArrayLength().Should().Be(0, role);
        }

        (await callers["Operator"].GetAsync($"/api/v1/sources/{sourceId}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await callers["Operator"].GetFromJsonAsync<JsonElement>("/api/v1/sources/kinds", Token)).EnumerateArray().Should().NotContain(item => item.GetProperty("kind").GetString() == "Conversations");
        (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/sources/kinds", Token)).EnumerateArray().Should().Contain(item => item.GetProperty("kind").GetString() == "Conversations");
        var hits = (await callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/search?q={word}", Token)).EnumerateArray().ToList();
        hits.Should().NotBeEmpty("the conversation was just taken and is searchable");
        var path = hits[0].GetProperty("path").GetString()!;
        (await callers["Reader"].GetAsync($"/api/v1/notes/{sourceId}/{path}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await callers["Assistant"].GetAsync($"/api/v1/notes/{sourceId}/{path}", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var mcp = await callers["Reader"].PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "search", arguments = new { query = word } } }, Token);
        var reply = JsonDocument.Parse(await mcp.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("result");
        reply.GetProperty("isError").GetBoolean().Should().BeFalse();
        reply.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("[]", "a reader key finds nothing of the conversations over MCP either");
    }
}
