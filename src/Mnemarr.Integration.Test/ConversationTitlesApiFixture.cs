using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The titles of the conversations over HTTP (ADR 0028): a person gives one, a model writes one, and who may do either.</summary>
public sealed class ConversationTitlesApiFixture : IClassFixture<TitlesFactory>, IClassFixture<MnemarrFactory>
{
    private readonly TitlesFactory _factory;
    private readonly MnemarrFactory _withoutModel;

    public ConversationTitlesApiFixture(TitlesFactory factory, MnemarrFactory withoutModel)
    {
        _factory = factory;
        _withoutModel = withoutModel;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Line(string type, string text, string at) =>
        JsonSerializer.Serialize(new { type, timestamp = at, message = new { role = type, content = text } });

    /// <summary>Takes a conversation of two turns and gives the path of its note.</summary>
    private static async Task<string> TalkAsync(Callers callers)
    {
        var machine = Callers.Unique("pc");
        var session = Callers.Unique("s");
        var lines = new[] { Line("user", "How do I rotate the tokens of the service accounts?", "2026-11-04T14:03:00Z"), Line("assistant", "Create a new key, then revoke the old one.", "2026-11-04T14:04:00Z") };
        var sent = await callers["Collector"].PostAsJsonAsync("/api/v1/ingest/conversations", new { format = "claude-code", machine, application = "Mnemarr", session, from = 0, lines }, Token);
        sent.StatusCode.Should().Be(HttpStatusCode.OK);
        return $"{machine}/mnemarr/2026-11-04-{session}.md";
    }

    private static async Task<string> NoteTextAsync(Callers callers, string path)
    {
        var sources = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/sources", Token);
        var id = sources.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "Conversations").GetProperty("id").GetGuid();
        var note = await callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/notes/{id}/{path}", Token);
        return note.GetProperty("raw").GetString()!;
    }

    private static string TitleUrl(string path) => $"/api/v1/conversations/title?path={Uri.EscapeDataString(path)}";

    [Fact]
    public async Task should_let_an_administrator_give_a_conversation_a_title_of_their_own_that_the_note_and_the_assistants_see()
    {
        var callers = await Callers.ForAsync(_factory);
        var path = await TalkAsync(callers);
        (await callers["Assistant"].GetFromJsonAsync<JsonElement>(TitleUrl(path), Token)).GetProperty("source").GetString().Should().Be("first-sentence");

        var response = await callers.Admin.PutAsJsonAsync("/api/v1/conversations/title", new { path, title = "Rotation of the service keys" }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        (body.GetProperty("title").GetString(), body.GetProperty("source").GetString()).Should().Be(("Rotation of the service keys", "person"));
        (await callers["Assistant"].GetFromJsonAsync<JsonElement>(TitleUrl(path), Token)).GetProperty("title").GetString().Should().Be("Rotation of the service keys");
        (await NoteTextAsync(callers, path)).Should().Contain("\n# Rotation of the service keys\n");
    }

    [Fact]
    public async Task should_let_only_those_who_may_write_conversations_change_a_title_and_those_who_may_read_them_see_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var path = await TalkAsync(callers);

        foreach (var role in new[] { "Assistant", "Operator", "Reader", "Monitor", "Collector" })
        {
            (await callers[role].PutAsJsonAsync("/api/v1/conversations/title", new { path, title = "Nope" }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await callers[role].PostAsJsonAsync("/api/v1/conversations/title/rewrite", new { path }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await callers[role].PostAsJsonAsync("/api/v1/conversations/titles/rewrite", new { }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }

        foreach (var role in new[] { "Operator", "Reader", "Monitor", "Collector" })
        {
            (await callers[role].GetAsync(TitleUrl(path), Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} may not read conversations");
            (await callers[role].GetAsync("/api/v1/conversations/titles", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
    }

    [Fact]
    public async Task should_have_the_model_write_a_title_and_a_summary_on_request_and_show_them_in_the_note()
    {
        var callers = await Callers.ForAsync(_factory);
        var path = await TalkAsync(callers);

        var response = await callers.Admin.PostAsJsonAsync("/api/v1/conversations/title/rewrite", new { path }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        (body.GetProperty("title").GetString(), body.GetProperty("source").GetString(), body.GetProperty("summary").GetString()).Should().Be(("The model's title", "summary", "The model's summary."));
        body.GetProperty("modelEnabled").GetBoolean().Should().BeTrue();
        (await NoteTextAsync(callers, path)).Should().Contain("# The model's title\n\n> The model's summary.");
        _factory.Writer.Asked.Should().Contain(asked => asked.Contains("[Person] How do I rotate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task should_count_the_titles_by_origin_and_queue_the_first_sentences_again_for_the_model()
    {
        var callers = await Callers.ForAsync(_factory);
        var byPerson = await TalkAsync(callers);
        var byModel = await TalkAsync(callers);
        await callers.Admin.PutAsJsonAsync("/api/v1/conversations/title", new { path = byPerson, title = "Mine" }, Token);
        await callers.Admin.PostAsJsonAsync("/api/v1/conversations/title/rewrite", new { path = byModel }, Token);

        var counts = await callers["Assistant"].GetFromJsonAsync<JsonElement>("/api/v1/conversations/titles", Token);
        counts.GetProperty("modelEnabled").GetBoolean().Should().BeTrue();
        counts.GetProperty("person").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        counts.GetProperty("summary").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        var queued = await callers.Admin.PostAsJsonAsync("/api/v1/conversations/titles/rewrite", new { includePerson = false }, Token);

        queued.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await queued.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("queued").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        (await callers.Admin.GetFromJsonAsync<JsonElement>(TitleUrl(byPerson), Token)).GetProperty("source").GetString().Should().Be("person", "what a person typed is kept");
        (await callers.Admin.GetFromJsonAsync<JsonElement>(TitleUrl(byModel), Token)).GetProperty("source").GetString().Should().Be("first-sentence");
    }

    [Fact]
    public async Task should_say_that_a_model_is_not_turned_on_and_still_let_a_person_write_a_title()
    {
        var callers = await Callers.ForAsync(_withoutModel);
        var path = await TalkAsync(callers);

        (await callers.Admin.PostAsJsonAsync("/api/v1/conversations/title/rewrite", new { path }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers.Admin.PostAsJsonAsync("/api/v1/conversations/titles/rewrite", new { }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/conversations/titles", Token)).GetProperty("modelEnabled").GetBoolean().Should().BeFalse();
        (await callers.Admin.PutAsJsonAsync("/api/v1/conversations/title", new { path, title = "Still mine to write" }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task should_refuse_a_path_that_is_missing_unknown_or_a_title_with_nothing_in_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var path = await TalkAsync(callers);

        (await callers.Admin.GetAsync("/api/v1/conversations/title", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers.Admin.GetAsync(TitleUrl("pc/none/nothing.md"), Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await callers.Admin.PutAsJsonAsync("/api/v1/conversations/title", new { path, title = "[[]]" }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callers.Admin.PutAsJsonAsync("/api/v1/conversations/title", new { path = "pc/none/nothing.md", title = "T" }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
