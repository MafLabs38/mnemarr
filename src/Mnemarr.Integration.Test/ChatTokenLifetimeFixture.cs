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
    public async Task should_stop_the_token_when_it_is_unknown_or_has_expired_and_when_the_person_is_disabled()
    {
        var (person, accountId) = await PersonAsync();
        var (_, token) = await SendAsync(person);
        (await ToolsAsync(token)).Should().Be(HttpStatusCode.OK);

        (await ToolsAsync("mnc_" + new string('x', 43))).Should().Be(HttpStatusCode.Unauthorized, "unknown");
        _factory.Clock.Advance(ChatAccess.TokenLifetime + TimeSpan.FromMinutes(1));
        (await ToolsAsync(token)).Should().Be(HttpStatusCode.Unauthorized, "expired");

        var (disabledPerson, disabledId) = await PersonAsync();
        var (_, fresh) = await SendAsync(disabledPerson);
        using var admin = await _factory.SignedInClientAsync();
        (await admin.PutAsJsonAsync($"/api/v1/accounts/{disabledId}", new { role = "Reader", enabled = false }, Token)).IsSuccessStatusCode.Should().BeTrue();
        (await ToolsAsync(fresh)).Should().Be(HttpStatusCode.Unauthorized, "the person is disabled");
        accountId.Should().NotBe(disabledId);
    }

    [Fact]
    public async Task should_keep_the_earlier_tokens_of_a_conversation_alive_when_a_message_is_sent_again()
    {
        var (person, _) = await PersonAsync();
        var (conversation, first) = await SendAsync(person);
        _factory.Clock.Advance(TimeSpan.FromMinutes(20));

        await SendAsync(person, conversation);
        _factory.Clock.Advance(TimeSpan.FromMinutes(20));

        (await ToolsAsync(first)).Should().Be(HttpStatusCode.OK, "a process that the runner started with the first token is still running");
    }

    [Fact]
    public async Task should_stop_a_token_twelve_hours_after_it_was_made_however_often_the_conversation_goes_on()
    {
        var (person, _) = await PersonAsync();
        var (conversation, first) = await SendAsync(person);

        // The person comes back every 25 minutes: their session lives on, and so does the conversation's token, until its twelve hours are over.
        for (var step = 0; step < 30; step++)
        {
            _factory.Clock.Advance(TimeSpan.FromMinutes(25));
            await SendAsync(person, conversation);
        }

        (await ToolsAsync(first)).Should().Be(HttpStatusCode.Unauthorized, "the token is older than twelve hours");
    }
}
