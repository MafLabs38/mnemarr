using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mnemarr.Core.Chat;

namespace Mnemarr.Integration.Test;

/// <summary>
/// How long the token that the chat gives its model lives (ADR 0035). These tests let time pass on the clock of the host, which also ends the sessions of everybody who signed in
/// before: they have a host of their own and each signs in its own person when it starts.
/// </summary>
public sealed class ChatTokenLifetimeFixture : IClassFixture<ChatFactory>
{
    private readonly ChatFactory _factory;

    public ChatTokenLifetimeFixture(ChatFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A person of their own, signed in now.</summary>
    private async Task<(HttpClient Client, Guid Id)> PersonAsync()
    {
        using var admin = await _factory.SignedInClientAsync();
        var name = Callers.Unique("lifetime");
        var id = await Callers.CreateAsync(admin, new { name, kind = "User", role = "Reader", password = Callers.Password });
        return (await _factory.SignedInClientAsync(name, Callers.Password), id);
    }

    private async Task<(string ConversationId, string Token)> SendAsync(HttpClient person, string? conversationId = null)
    {
        if (conversationId is null)
        {
            var created = await person.PostAsJsonAsync("/api/v1/chat/conversations", new { agent = "claude-code", model = "sonnet" }, Token);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            conversationId = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetString()!;
        }

        (await person.PostAsJsonAsync($"/api/v1/chat/conversations/{conversationId}/messages", new { text = "hello" }, Token)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = _factory.Runner.Requests.Last(request => request.Path.EndsWith($"{conversationId}/messages", StringComparison.Ordinal)).Body!;
        return (conversationId, JsonNode.Parse(body)!["mcpToken"]!.GetValue<string>());
    }

    private async Task<HttpStatusCode> ToolsAsync(string token)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);
        return (await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, Token)).StatusCode;
    }

    [Fact]
    public async Task should_not_open_the_mcp_server_with_a_token_it_never_made()
    {
        (await ToolsAsync("mnc_" + new string('x', 43))).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_stop_the_token_thirty_minutes_after_the_last_message()
    {
        var (person, _) = await PersonAsync();
        var (_, token) = await SendAsync(person);
        (await ToolsAsync(token)).Should().Be(HttpStatusCode.OK);

        _factory.Clock.Advance(ChatAccess.TokenLifetime + TimeSpan.FromMinutes(1));

        (await ToolsAsync(token)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_stop_the_token_when_the_person_is_disabled()
    {
        var (person, id) = await PersonAsync();
        var (_, token) = await SendAsync(person);
        using var admin = await _factory.SignedInClientAsync();

        (await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader", enabled = false }, Token)).IsSuccessStatusCode.Should().BeTrue();

        (await ToolsAsync(token)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_forget_the_conversations_the_runner_no_longer_has_with_their_tokens()
    {
        var (person, _) = await PersonAsync();
        var (conversation, token) = await SendAsync(person);
        (await ToolsAsync(token)).Should().Be(HttpStatusCode.OK);
        _factory.Runner.Forget(conversation);

        // Just made: a list asked for before it existed must not take it away.
        (await person.GetFromJsonAsync<JsonElement>("/api/v1/chat/conversations", Token)).GetArrayLength().Should().Be(0);
        (await ToolsAsync(token)).Should().Be(HttpStatusCode.OK, "it is younger than the grace");

        _factory.Clock.Advance(ChatAccess.NewConversationGrace + TimeSpan.FromMinutes(1));
        var signedAgain = await PersonAsync();
        _ = await signedAgain.Client.GetFromJsonAsync<JsonElement>("/api/v1/chat/conversations", Token);

        (await ToolsAsync(token)).Should().Be(HttpStatusCode.Unauthorized, "the model of a conversation that is gone has no hands");
    }
}
