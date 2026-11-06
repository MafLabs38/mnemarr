using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mnemarr.Integration.Test;

/// <summary>What the tests of the enrolment share: adding a machine, joining it, reading its state, and the MCP calls.</summary>
public abstract class EnrolmentFixtureBase
{
    protected const string Enrol = "/api/v1/ingest/enrol";
    protected readonly EnrolledOnlyFactory _factory;

    protected EnrolmentFixtureBase(EnrolledOnlyFactory factory)
    {
        _factory = factory;
    }

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    protected static string Line(string text) =>
        JsonSerializer.Serialize(new { type = "user", timestamp = "2026-11-04T14:03:00Z", message = new { role = "user", content = text } });

    protected static object Batch(string machine, string session, params string[] lines) =>
        new { format = "claude-code", machine, application = "Mnemarr", session, from = 0, lines };

    protected async Task<JsonElement> AddAsync(HttpClient admin, string machine, string kind = "intermittent")
    {
        var response = await admin.PostAsJsonAsync("/api/v1/enrolments", new { machine, kind }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    protected async Task<HttpClient> JoinAsync(HttpClient admin, string machine, string kind = "intermittent")
    {
        var code = (await AddAsync(admin, machine, kind)).GetProperty("code").GetString();
        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine }, Token);
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK);
        return _factory.CreateKeyClient((await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("key").GetString()!);
    }

    protected async Task<JsonElement> StatusOfAsync(HttpClient admin, string machine) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token)).EnumerateArray().Single(item => item.GetProperty("machine").GetString() == machine);

    protected async Task<string> ReadyAsync(HttpClient client) => await (await client.GetAsync("/health/ready", Token)).Content.ReadAsStringAsync(Token);

    protected static async Task<JsonNode> RpcAsync(HttpClient client, string method, object parameters)
    {
        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
    }

    protected static async Task<JsonNode> CallAsync(HttpClient client, string tool, object arguments)
    {
        var reply = await RpcAsync(client, "tools/call", new { name = tool, arguments });
        reply["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
        return JsonNode.Parse(reply["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
    }
}
