using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mnemarr.Integration.Test;

/// <summary>The chat over HTTP (ADR 0035): whose a conversation is, what the runner is sent, and what the token it gives the model may do.</summary>
public sealed class ChatApiFixture : IClassFixture<ChatFactory>, IClassFixture<MnemarrFactory>
{
    private readonly ChatFactory _factory;
    private readonly MnemarrFactory _off;

    public ChatApiFixture(ChatFactory factory, MnemarrFactory off)
    {
        _factory = factory;
        _off = off;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> StartAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/v1/chat/conversations", new { agent = "claude-code", model = "sonnet" }, Token);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetString()!;
    }

    /// <summary>Sends a message and gives the token that went to the runner with it.</summary>
    private async Task<string> SendAsync(HttpClient client, string id)
    {
        var sent = await client.PostAsJsonAsync($"/api/v1/chat/conversations/{id}/messages", new { text = "What do my notes say?" }, Token);
        sent.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = _factory.Runner.Requests.Last(request => request.Path.EndsWith($"{id}/messages", StringComparison.Ordinal)).Body!;
        return JsonNode.Parse(body)!["mcpToken"]!.GetValue<string>();
    }

    private HttpClient WithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);
        return client;
    }

    private static async Task<JsonNode> RpcAsync(HttpClient client, string method, object? parameters = null)
    {
        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
    }

    [Fact]
    public async Task should_say_which_agents_the_runner_offers_and_that_the_tools_that_change_things_are_on()
    {
        var callers = await Callers.ForAsync(_factory);

        var status = await callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/chat", Token);

        status.GetProperty("enabled").GetBoolean().Should().BeTrue();
        status.GetProperty("reachable").GetBoolean().Should().BeTrue();
        status.GetProperty("mcpWriteTools").GetBoolean().Should().BeTrue();
        status.GetProperty("agents")[0].GetProperty("id").GetString().Should().Be("claude-code");
    }

    [Fact]
    public async Task should_be_off_and_answer_404_when_no_runner_is_given()
    {
        var callers = await Callers.ForAsync(_off);

        (await callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/chat", Token)).GetProperty("enabled").GetBoolean().Should().BeFalse();
        (await callers["Reader"].GetAsync("/api/v1/chat/conversations", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Reader", HttpStatusCode.OK)]
    [InlineData("Operator", HttpStatusCode.OK)]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Assistant", HttpStatusCode.Forbidden)]
    [InlineData("Collector", HttpStatusCode.Forbidden)]
    [InlineData("Monitor", HttpStatusCode.Forbidden)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    public async Task should_be_for_the_people_whose_role_gives_the_chat(string role, HttpStatusCode expected)
    {
        var callers = await Callers.ForAsync(_factory);
        var client = role == "anonymous" ? callers.Anonymous : callers[role];

        (await client.GetAsync("/api/v1/chat/conversations", Token)).StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task should_keep_a_conversation_for_the_person_who_started_it_and_reveal_nothing_to_another()
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await StartAsync(callers["Reader"]);

        (await callers["Reader"].GetAsync($"/api/v1/chat/conversations/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.Accepted, "the runner's answer is passed through");
        foreach (var other in new[] { "Operator", "Admin" })
        {
            (await callers[other].GetAsync($"/api/v1/chat/conversations/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound, other);
            (await callers[other].PostAsJsonAsync($"/api/v1/chat/conversations/{id}/messages", new { text = "hi" }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await callers[other].PostAsJsonAsync($"/api/v1/chat/conversations/{id}/permissions/r1", new { approve = true }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await callers[other].PostAsJsonAsync($"/api/v1/chat/conversations/{id}/cancel", new { }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await callers[other].GetAsync($"/api/v1/chat/conversations/{id}/events", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await callers[other].DeleteAsync($"/api/v1/chat/conversations/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await callers[other].GetFromJsonAsync<JsonElement>("/api/v1/chat/conversations", Token)).EnumerateArray().Select(item => item.GetProperty("id").GetString()).Should().NotContain(id);
        }

        (await callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/chat/conversations", Token)).EnumerateArray().Select(item => item.GetProperty("id").GetString()).Should().Contain(id);
    }

    [Fact]
    public async Task should_not_forward_what_the_browser_sent_and_speak_to_the_runner_with_its_secret_only()
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await StartAsync(callers["Reader"]);

        await SendAsync(callers["Reader"], id);

        _factory.Runner.Requests.Should().OnlyContain(request => request.Authorization == "Bearer " + new string('s', 40));
        _factory.Runner.Requests.Last().Body.Should().NotContain("X-Mnemarr-Request");
        // Nothing of the browser's request goes on: no cookie, no header of the interface, no key.
        _factory.Runner.HeaderNames.Should().OnlyContain(name => name == "Authorization" || name == "Content-Type" || name == "Content-Length" || name == "Accept" || name == "Last-Event-ID");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task should_refuse_an_empty_message(string text)
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await StartAsync(callers["Reader"]);

        (await callers["Reader"].PostAsJsonAsync($"/api/v1/chat/conversations/{id}/messages", new { text }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_refuse_a_model_that_is_not_a_name_and_a_message_that_is_too_long()
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await StartAsync(callers["Reader"]);

        (await callers["Reader"].PostAsJsonAsync($"/api/v1/chat/conversations/{id}/messages", new { text = "hi", model = "Bad Model; rm" }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers["Reader"].PostAsJsonAsync($"/api/v1/chat/conversations/{id}/messages", new { text = new string('x', Mnemarr.Api.V1.Chat.ChatController.MaxMessageLength + 1) }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_say_a_runner_that_is_down_or_refuses_the_secret_is_a_bad_gateway()
    {
        var callers = await Callers.ForAsync(_factory);
        try
        {
            _factory.Runner.Down = true;
            (await callers["Reader"].GetAsync("/api/v1/chat/conversations", Token)).StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/chat", Token)).GetProperty("reachable").GetBoolean().Should().BeFalse();
            _factory.Runner.Down = false;
            _factory.Runner.Refuses = HttpStatusCode.Unauthorized;
            (await callers["Reader"].GetAsync("/api/v1/chat/conversations", Token)).StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally
        {
            _factory.Runner.Down = false;
            _factory.Runner.Refuses = null;
        }
    }

    [Fact]
    public async Task should_keep_the_chat_for_people_even_when_a_service_has_the_role_that_gives_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await Callers.CreateAsync(callers.Admin, new { name = Callers.Unique("robot"), kind = "Service", role = "Reader" });
        var robot = _factory.CreateKeyClient(await Callers.CreateKeyAsync(callers.Admin, id));

        (await robot.GetAsync("/api/v1/chat/conversations", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await robot.PostAsJsonAsync("/api/v1/chat/conversations", new { agent = "claude-code", model = "sonnet" }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task should_stop_a_person_from_keeping_more_than_fifty_conversations()
    {
        var callers = await Callers.ForAsync(_factory);
        var name = Callers.Unique("collector-of-chats");
        await Callers.CreateAsync(callers.Admin, new { name, kind = "User", role = "Reader", password = Callers.Password });
        var person = await _factory.SignedInClientAsync(name, Callers.Password);
        for (var count = 0; count < Mnemarr.Api.V1.Chat.ChatService.MaxConversationsPerPerson; count++)
        {
            await StartAsync(person);
        }

        var refused = await person.PostAsJsonAsync("/api/v1/chat/conversations", new { agent = "claude-code", model = "sonnet" }, Token);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await refused.Content.ReadAsStringAsync(Token)).Should().Contain("delete one first");
    }

    [Fact]
    public async Task should_forget_the_conversations_that_the_runner_no_longer_has_with_their_tokens()
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await StartAsync(callers["Reader"]);
        var token = await SendAsync(callers["Reader"], id);
        (await RpcAsync(WithToken(token), "tools/list"))["result"].Should().NotBeNull();

        _factory.Runner.Forget(id);
        var listed = await callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/chat/conversations", Token);

        listed.EnumerateArray().Select(item => item.GetProperty("id").GetString()).Should().NotContain(id);
        (await callers["Reader"].GetAsync($"/api/v1/chat/conversations/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await WithToken(token).PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the model of a conversation that is gone has no hands");
    }

    [Fact]
    public async Task should_answer_a_bad_gateway_when_the_stream_does_not_start_in_time()
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await StartAsync(callers["Reader"]);
        try
        {
            _factory.Runner.Stalls = true;
            (await callers["Reader"].GetAsync($"/api/v1/chat/conversations/{id}/events", Token)).StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally
        {
            _factory.Runner.Stalls = false;
        }
    }

    [Fact]
    public async Task should_forget_a_conversation_that_is_deleted()
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await StartAsync(callers["Reader"]);

        (await callers["Reader"].DeleteAsync($"/api/v1/chat/conversations/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await callers["Reader"].GetAsync($"/api/v1/chat/conversations/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_give_the_model_a_token_that_acts_as_the_person_and_only_as_far_as_their_role_goes()
    {
        var callers = await Callers.ForAsync(_factory);
        var reader = WithToken(await SendAsync(callers["Reader"], await StartAsync(callers["Reader"])));
        var operatorClient = WithToken(await SendAsync(callers["Operator"], await StartAsync(callers["Operator"])));

        var readerTools = (await RpcAsync(reader, "tools/list"))["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        var operatorTools = (await RpcAsync(operatorClient, "tools/list"))["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();

        readerTools.Should().Contain(["search", "read_note"]).And.NotContain(["create_note", "create_source", "get_status"], "a reader's role gives no writing and no status");
        operatorTools.Should().Contain(["search", "create_note", "create_source", "index_source", "get_status"]).And.NotContain("request_collector_enrolment");
    }

    [Fact]
    public async Task should_open_the_mcp_server_and_nothing_else_with_the_token()
    {
        var callers = await Callers.ForAsync(_factory);
        var token = WithToken(await SendAsync(callers["Admin"], await StartAsync(callers["Admin"])));

        (await token.GetAsync("/api/v1/notes", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await token.GetAsync("/api/v1/accounts", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await token.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task should_write_the_audit_as_the_person_through_the_chat_when_the_model_changes_something()
    {
        var callers = await Callers.ForAsync(_factory);
        var id = await StartAsync(callers["Operator"]);
        var client = WithToken(await SendAsync(callers["Operator"], id));
        var folder = Path.Combine(_factory.DataFolder, "sources", Callers.Unique("chatsrc"));
        Directory.CreateDirectory(folder);
        var name = Callers.Unique("chat-vault");

        var created = await RpcAsync(client, "tools/call", new { name = "create_source", arguments = new { name, kind = "Knowledge", mode = "ReadWrite", path = folder } });

        created["result"]!["isError"]?.GetValue<bool>().Should().NotBe(true);
        var audit = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=50", Token);
        audit.EnumerateArray().Any(entry => entry.GetProperty("actor").GetString()!.Contains($"(via chat:{id})", StringComparison.Ordinal)).Should().BeTrue("the log says that the chat acted for the person");
    }
}
