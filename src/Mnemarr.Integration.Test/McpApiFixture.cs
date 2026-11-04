using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mnemarr.Integration.Test;

/// <summary>The MCP server over HTTP: the protocol, what each key sees, and the tools that write only when the operator allows them.</summary>
public sealed class McpApiFixture : IClassFixture<MnemarrFactory>, IClassFixture<McpWriteFactory>
{
    private readonly MnemarrFactory _readOnly;
    private readonly McpWriteFactory _writable;

    public McpApiFixture(MnemarrFactory readOnly, McpWriteFactory writable)
    {
        _readOnly = readOnly;
        _writable = writable;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<HttpClient> KeyAsync(MnemarrFactoryBase factory, string role, Guid? onlySource = null)
    {
        using var admin = await factory.SignedInClientAsync();
        var account = await Callers.CreateAsync(admin, new { name = Callers.Unique(role.ToLowerInvariant()), kind = "Service", role });
        var response = await admin.PostAsJsonAsync($"/api/v1/accounts/{account}/keys", new { name = "mcp", sources = onlySource is { } id ? new[] { id } : null }, Token);
        return factory.CreateKeyClient((await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);
    }

    private static async Task<Guid> SourceAsync(MnemarrFactoryBase factory, string name, string mode, params (string Path, string Text)[] notes)
    {
        using var admin = await factory.SignedInClientAsync();
        var folder = Path.Combine(factory.DataFolder, "sources", name);
        foreach (var (path, text) in notes)
        {
            var file = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        Directory.CreateDirectory(folder);
        var created = await admin.PostAsJsonAsync("/api/v1/sources", new { name, kind = "Knowledge", mode, path = folder }, Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/sources/{id}/index", null, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    private static async Task<JsonNode> RpcAsync(HttpClient client, string method, object? parameters = null, int id = 1)
    {
        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id, method, @params = parameters }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
    }

    private static async Task<(bool IsError, JsonNode Content)> CallAsync(HttpClient client, string tool, object arguments)
    {
        var reply = await RpcAsync(client, "tools/call", new { name = tool, arguments });
        var result = reply["result"]!;
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        return (result["isError"]!.GetValue<bool>(), result["isError"]!.GetValue<bool>() ? JsonValue.Create(text)! : JsonNode.Parse(text)!);
    }

    private static string[] ToolNames(JsonNode reply) => [.. reply["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>())];

    [Fact]
    public async Task should_answer_nobody_who_is_not_signed_in()
    {
        using var anonymous = _readOnly.CreateBrowserClient();

        var response = await anonymous.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_not_take_a_cross_site_request_that_rides_on_a_session_cookie()
    {
        using var browser = await _writable.SignedInClientAsync();
        browser.DefaultRequestHeaders.Remove("X-Mnemarr-Request");
        var forged = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", System.Text.Encoding.UTF8, "text/plain");

        var response = await browser.PostAsync("/mcp", forged, Token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a form from another site cannot add the header, and must not reach the tools with the person's cookie");
    }

    [Fact]
    public async Task should_take_only_json()
    {
        using var client = await KeyAsync(_readOnly, "Reader");

        var response = await client.PostAsync("/mcp", new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", System.Text.Encoding.UTF8, "text/plain"), Token);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task should_speak_the_protocol_initialize_ping_notifications_and_what_it_does_not_offer()
    {
        using var client = await KeyAsync(_readOnly, "Reader");

        var initialized = await RpcAsync(client, "initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        var unknownVersion = await RpcAsync(client, "initialize", new { protocolVersion = "1999-01-01" });
        var ping = await RpcAsync(client, "ping");
        var unknown = await RpcAsync(client, "resources/list");
        var notification = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" }, Token);
        var get = await client.GetAsync("/mcp", Token);
        var garbage = await client.PostAsync("/mcp", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"), Token);

        initialized["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-03-26");
        initialized["result"]!["capabilities"]!["tools"].Should().NotBeNull();
        initialized["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("mnemarr");
        unknownVersion["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-06-18");
        ping["result"].Should().NotBeNull();
        unknown["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
        notification.StatusCode.Should().Be(HttpStatusCode.Accepted);
        get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        get.Content.Headers.Allow.Should().Contain("POST");
        garbage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_offer_each_key_only_the_tools_its_permissions_allow_and_no_writing_tool_unless_the_operator_turned_them_on()
    {
        using var reader = await KeyAsync(_readOnly, "Reader");
        using var assistant = await KeyAsync(_readOnly, "Assistant");
        using var collector = await KeyAsync(_readOnly, "Collector");
        using var writingAssistant = await KeyAsync(_writable, "Assistant");
        using var writingReader = await KeyAsync(_writable, "Reader");

        var readerTools = ToolNames(await RpcAsync(reader, "tools/list"));
        var assistantTools = ToolNames(await RpcAsync(assistant, "tools/list"));
        var collectorTools = ToolNames(await RpcAsync(collector, "tools/list"));
        var writingTools = ToolNames(await RpcAsync(writingAssistant, "tools/list"));
        var writingReaderTools = ToolNames(await RpcAsync(writingReader, "tools/list"));

        string[] reading = ["list_sources", "search", "list_notes", "read_note", "list_tags"];
        readerTools.Should().BeEquivalentTo(reading);
        assistantTools.Should().BeEquivalentTo(reading, "the write tools are off by default");
        collectorTools.Should().BeEmpty();
        writingTools.Should().BeEquivalentTo([.. reading, "create_note", "update_note", "append_to_note", "rename_note", "delete_note"]);
        writingReaderTools.Should().BeEquivalentTo(reading, "a role that cannot write does not get the tools, whatever the switch says");
    }

    [Fact]
    public async Task should_search_list_and_read_what_the_key_may_and_nothing_of_the_other_sources()
    {
        var mine = await SourceAsync(_readOnly, Callers.Unique("mine"), "ReadOnly", ("Topic/Docker.md", "# Docker\nImages are made of layers. #infra\n"));
        var other = await SourceAsync(_readOnly, Callers.Unique("other"), "ReadOnly", ("secret.md", "# Secret\nlayers in the other source\n"));
        using var all = await KeyAsync(_readOnly, "Reader");
        using var scoped = await KeyAsync(_readOnly, "Reader", mine);

        var sources = await CallAsync(scoped, "list_sources", new { });
        var hits = await CallAsync(scoped, "search", new { query = "layers" });
        var allHits = await CallAsync(all, "search", new { query = "layers" });
        var notes = await CallAsync(scoped, "list_notes", new { });
        var read = await CallAsync(scoped, "read_note", new { source = mine.ToString(), path = "Topic/Docker.md" });
        var hidden = await CallAsync(scoped, "read_note", new { source = other.ToString(), path = "secret.md" });
        var tags = await CallAsync(scoped, "list_tags", new { });

        sources.Content.AsArray().Should().ContainSingle().Which!["writable"]!.GetValue<bool>().Should().BeFalse();
        hits.Content.AsArray().Select(hit => hit!["path"]!.GetValue<string>()).Should().Equal("Topic/Docker.md");
        allHits.Content.AsArray().Count.Should().BeGreaterThanOrEqualTo(2);
        notes.Content["notes"]!.AsArray().Select(note => note!["path"]!.GetValue<string>()).Should().Equal("Topic/Docker.md");
        read.Content["content"]!.GetValue<string>().Should().Contain("layers");
        read.Content["version"]!.GetValue<string>().Should().HaveLength(64);
        hidden.IsError.Should().BeTrue("a source out of scope is absent");
        tags.Content["tags"]!.AsArray().Select(tag => tag!["tag"]!.GetValue<string>()).Should().Contain("infra");
    }

    [Fact]
    public async Task should_answer_a_refusal_as_a_tool_error_the_assistant_can_read_and_never_as_a_failure_of_the_protocol()
    {
        using var client = await KeyAsync(_writable, "Assistant");
        var id = await SourceAsync(_writable, Callers.Unique("vault"), "ReadWrite");

        var missingArgument = await CallAsync(client, "search", new { });
        var unknownSource = await CallAsync(client, "read_note", new { source = "nope", path = "a.md" });
        var unknownTool = await RpcAsync(client, "tools/call", new { name = "drop_database", arguments = new { } });
        var noName = await RpcAsync(client, "tools/call", new { });
        _ = id;

        missingArgument.IsError.Should().BeTrue();
        missingArgument.Content.GetValue<string>().Should().Contain("query");
        unknownSource.IsError.Should().BeTrue();
        unknownTool["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        noName["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
    }

    [Fact]
    public async Task should_not_call_a_writing_tool_when_the_operator_did_not_turn_them_on()
    {
        using var client = await KeyAsync(_readOnly, "Assistant");
        var name = Callers.Unique("vault");
        await SourceAsync(_readOnly, name, "ReadWrite");

        var attempt = await CallAsync(client, "create_note", new { source = name, path = "a.md", content = "# A" });

        attempt.IsError.Should().BeTrue();
        File.Exists(Path.Combine(_readOnly.DataFolder, "sources", name, "a.md")).Should().BeFalse();
    }

    [Fact]
    public async Task should_write_through_the_tools_when_allowed_with_a_version_to_send_back_and_a_trail_in_the_audit_log()
    {
        using var client = await KeyAsync(_writable, "Assistant");
        var name = Callers.Unique("vault");
        await SourceAsync(_writable, name, "ReadWrite", ("Index.md", "# Index\n[[Docker]]\n"));

        var created = await CallAsync(client, "create_note", new { source = name, path = "Docker.md", content = "# Docker\nfirst\n" });
        var read = await CallAsync(client, "read_note", new { source = name, path = "Docker.md" });
        var version = read.Content["version"]!.GetValue<string>();
        var updated = await CallAsync(client, "update_note", new { source = name, path = "Docker.md", content = "# Docker\nsecond\n", version });
        var stale = await CallAsync(client, "update_note", new { source = name, path = "Docker.md", content = "# lost", version });
        var appended = await CallAsync(client, "append_to_note", new { source = name, path = "Docker.md", text = "third" });
        var renamed = await CallAsync(client, "rename_note", new { source = name, from = "Docker.md", to = "Engine.md" });
        var removed = await CallAsync(client, "delete_note", new { source = name, path = "Engine.md" });

        created.IsError.Should().BeFalse();
        updated.IsError.Should().BeFalse();
        stale.IsError.Should().BeTrue("the note changed since it was read");
        stale.Content.GetValue<string>().Should().Contain("changed");
        appended.IsError.Should().BeFalse();
        renamed.Content["notesRewritten"]!.GetValue<int>().Should().Be(1);
        removed.IsError.Should().BeFalse();
        File.ReadAllText(Path.Combine(_writable.DataFolder, "sources", name, "Index.md")).Should().Be("# Index\n[[Engine]]\n");
        File.Exists(Path.Combine(_writable.DataFolder, "sources", name, "Engine.md")).Should().BeFalse();
        using var admin = await _writable.SignedInClientAsync();
        (await admin.GetStringAsync("/api/v1/audit?action=note.updated", Token)).Should().Contain("Docker.md");
    }

    [Fact]
    public async Task should_not_write_in_a_source_that_is_read_only_even_through_a_tool()
    {
        using var client = await KeyAsync(_writable, "Assistant");
        var name = Callers.Unique("closed");
        await SourceAsync(_writable, name, "ReadOnly");

        var attempt = await CallAsync(client, "create_note", new { source = name, path = "a.md", content = "# A" });

        attempt.IsError.Should().BeTrue();
        attempt.Content.GetValue<string>().Should().Contain("read-only");
    }
}
