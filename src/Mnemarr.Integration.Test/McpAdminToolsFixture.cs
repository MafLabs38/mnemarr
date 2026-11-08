using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mnemarr.Integration.Test;

/// <summary>The tools that look after Mnemarr over MCP (ADR 0035): its state and its sources, under the permissions of the key, and the ones that change something only when the operator allows them.</summary>
public sealed class McpAdminToolsFixture : IClassFixture<MnemarrFactory>, IClassFixture<McpWriteFactory>
{
    private readonly MnemarrFactory _readOnly;
    private readonly McpWriteFactory _writable;

    public McpAdminToolsFixture(MnemarrFactory readOnly, McpWriteFactory writable)
    {
        _readOnly = readOnly;
        _writable = writable;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<HttpClient> KeyAsync(MnemarrFactoryBase factory, string role)
    {
        using var admin = await factory.SignedInClientAsync();
        var account = await Callers.CreateAsync(admin, new { name = Callers.Unique(role.ToLowerInvariant()), kind = "Service", role });
        return factory.CreateKeyClient(await Callers.CreateKeyAsync(admin, account));
    }

    private static async Task<JsonNode> RpcAsync(HttpClient client, string method, object? parameters = null)
    {
        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters }, Token);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
    }

    private static async Task<List<string>> ToolsAsync(HttpClient client) =>
        [.. (await RpcAsync(client, "tools/list"))["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>())];

    private static async Task<(bool IsError, JsonNode Content)> CallAsync(HttpClient client, string tool, object arguments)
    {
        var result = (await RpcAsync(client, "tools/call", new { name = tool, arguments }))["result"]!;
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        var isError = result["isError"]?.GetValue<bool>() == true;
        return (isError, isError ? JsonValue.Create(text)! : JsonNode.Parse(text)!);
    }

    [Fact]
    public async Task should_offer_the_status_to_a_key_that_may_read_it_and_the_changes_only_when_the_operator_allows_them()
    {
        var readOnly = await ToolsAsync(await KeyAsync(_readOnly, "Operator"));
        var writable = await ToolsAsync(await KeyAsync(_writable, "Operator"));

        readOnly.Should().Contain("get_status").And.NotContain(["create_source", "update_source", "delete_source", "index_source", "sync_source"]);
        writable.Should().Contain(["get_status", "create_source", "update_source", "delete_source", "index_source", "sync_source"]);
        (await ToolsAsync(await KeyAsync(_writable, "Assistant"))).Should().NotContain(["get_status", "create_source"], "an assistant's role has neither the status nor the sources");
    }

    [Fact]
    public async Task should_say_where_mnemarr_is_at()
    {
        var (isError, status) = await CallAsync(await KeyAsync(_readOnly, "Operator"), "get_status", new { });

        isError.Should().BeFalse();
        status["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        status["searchByMeaning"]!["state"]!.GetValue<string>().Should().Be("off");
        status["sources"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task should_declare_change_read_again_and_forget_a_source()
    {
        var client = await KeyAsync(_writable, "Operator");
        var name = Callers.Unique("vault");
        var folder = Path.Combine(_writable.DataFolder, "sources", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.md"), "# A\nA note.");

        var created = await CallAsync(client, "create_source", new { name, kind = "Knowledge", mode = "ReadOnly", path = folder });
        var indexed = await CallAsync(client, "index_source", new { source = name });
        var changed = await CallAsync(client, "update_source", new { source = name, mode = "ReadWrite" });
        var sources = await CallAsync(client, "list_sources", new { });
        var forgotten = await CallAsync(client, "delete_source", new { source = name });

        created.IsError.Should().BeFalse();
        created.Content["kind"]!.GetValue<string>().Should().Be("Knowledge");
        indexed.Content["added"]!.GetValue<int>().Should().Be(1);
        changed.Content["mode"]!.GetValue<string>().Should().Be("ReadWrite");
        sources.Content.AsArray().Any(source => source!["name"]!.GetValue<string>() == name && source["writable"]!.GetValue<bool>()).Should().BeTrue();
        forgotten.IsError.Should().BeFalse();
        File.Exists(Path.Combine(folder, "a.md")).Should().BeTrue("a source is forgotten, its folder is not touched");
    }

    [Theory]
    [InlineData("create_source", """{"name":"x","kind":"Conversations","mode":"ReadOnly","path":"/nowhere"}""")]
    [InlineData("create_source", """{"name":"x","kind":"Knowledge","mode":"Sometimes","path":"/nowhere"}""")]
    [InlineData("create_source", """{"name":"x","kind":"Knowledge","mode":"ReadOnly","path":"/etc"}""")]
    [InlineData("delete_source", """{"source":"no-such-source"}""")]
    [InlineData("index_source", """{"source":"no-such-source"}""")]
    public async Task should_refuse_what_the_rest_api_would_refuse(string tool, string arguments)
    {
        var (isError, content) = await CallAsync(await KeyAsync(_writable, "Operator"), tool, JsonSerializer.Deserialize<JsonElement>(arguments));

        isError.Should().BeTrue();
        content.GetValue<string>().Should().NotBeNullOrWhiteSpace("the model is told why, so that it can say it");
    }

    [Fact]
    public void should_never_give_a_tool_that_changes_something_the_name_of_a_read()
    {
        // The runner of the chat lets a tool run without asking when its name starts with get_, list_, find_ or read_ (or is "search"): a tool that changes something must never match.
        // The other way is safe: a tool that only asks for a change that a person approves later (request_collector_enrolment) is a read by Mnemarr and a card by the runner.
        var reads = new[] { "get_", "list_", "find_", "read_" };
        foreach (var tool in Mnemarr.Api.V1.Mcp.McpTools.All)
        {
            var looksLikeARead = tool.Name == "search" || reads.Any(prefix => tool.Name.StartsWith(prefix, StringComparison.Ordinal));
            (looksLikeARead && tool.Writes).Should().BeFalse($"the runner would let {tool.Name} run without asking, and it changes something");
        }
    }

    [Fact]
    public async Task should_read_a_source_again_and_report_what_it_synchronised()
    {
        var client = await KeyAsync(_writable, "Operator");
        var name = Callers.Unique("again");
        var folder = Path.Combine(_writable.DataFolder, "sources", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.md"), "# A\nA note.");
        (await CallAsync(client, "create_source", new { name, kind = "Knowledge", mode = "ReadOnly", path = folder })).IsError.Should().BeFalse();

        var synced = await CallAsync(client, "sync_source", new { source = name });

        synced.IsError.Should().BeTrue("a folder that is not a Git repository has nothing to synchronise");
        synced.Content.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task should_not_let_a_key_of_a_role_without_the_sources_call_the_tools_that_look_after_them()
    {
        var (isError, _) = await CallAsync(await KeyAsync(_writable, "Assistant"), "create_source", new { name = "x", kind = "Knowledge", mode = "ReadOnly", path = "/tmp" });

        isError.Should().BeTrue("there is no such tool for this caller");
    }

    [Fact]
    public async Task should_still_let_an_assistant_ask_for_a_machine()
    {
        (await ToolsAsync(await KeyAsync(_writable, "Assistant"))).Should().Contain("request_collector_enrolment");
    }
}
