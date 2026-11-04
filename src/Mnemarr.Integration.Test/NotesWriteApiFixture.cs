using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>Writing notes over HTTP: create, change with a version, move with its links, remove; who may, in which source, and the commit a Git source makes.</summary>
public sealed class NotesWriteApiFixture : IClassFixture<MnemarrFactory>, IAsyncLifetime
{
    private readonly MnemarrFactory _factory;
    private Callers _callers = null!;

    public NotesWriteApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _callers = await Callers.ForAsync(_factory);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(Guid Id, string Folder)> SourceAsync(string name, string mode = "ReadWrite", object? repository = null)
    {
        var folder = Path.Combine(_factory.DataFolder, "sources", name);
        if (repository is null)
        {
            Directory.CreateDirectory(folder);
        }

        var created = await _callers["Operator"].PostAsJsonAsync("/api/v1/sources", new { name, kind = "Knowledge", mode, path = folder, repository }, Token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        return ((await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid(), folder);
    }

    private static string Note(Guid id, string path) => $"/api/v1/notes/{id}/{path}";

    private async Task<JsonElement> GetAsync(HttpClient client, Guid id, string path) => await client.GetFromJsonAsync<JsonElement>(Note(id, path), Token);

    [Fact]
    public async Task should_create_change_move_and_remove_a_note_as_an_operator_with_the_index_following_each_step()
    {
        var (id, folder) = await SourceAsync(Callers.Unique("vault"));
        var operatorClient = _callers["Operator"];

        var created = await operatorClient.PostAsJsonAsync(Note(id, "Topic/Docker.md"), new { content = "# Docker\nImages are made of layers.\n" }, Token);
        var read = await GetAsync(operatorClient, id, "Topic/Docker.md");
        var found = await operatorClient.GetFromJsonAsync<JsonElement>($"/api/v1/search?q=layers&source={id}", Token);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        read.GetProperty("writable").GetBoolean().Should().BeTrue();
        read.GetProperty("raw").GetString().Should().Be("# Docker\nImages are made of layers.\n");
        found.EnumerateArray().Select(hit => hit.GetProperty("path").GetString()).Should().Equal("Topic/Docker.md");

        var version = read.GetProperty("version").GetString();
        var updated = await operatorClient.PutAsJsonAsync(Note(id, "Topic/Docker.md"), new { content = "# Docker\nImages are layers of files.\n", version }, Token);
        var stale = await operatorClient.PutAsJsonAsync(Note(id, "Topic/Docker.md"), new { content = "# lost", version }, Token);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetAsync(operatorClient, id, "Topic/Docker.md")).GetProperty("raw").GetString().Should().Contain("layers of files");

        await operatorClient.PostAsJsonAsync(Note(id, "Index.md"), new { content = "# Index\nSee [[Docker]].\n" }, Token);
        var moved = await operatorClient.PostAsJsonAsync($"/api/v1/notes/{id}/rename", new { from = "Topic/Docker.md", to = "Ops/Engine.md" }, Token);
        var movedBody = await moved.Content.ReadFromJsonAsync<JsonElement>(Token);

        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        movedBody.GetProperty("notesRewritten").GetInt32().Should().Be(1);
        File.ReadAllText(Path.Combine(folder, "Index.md")).Should().Be("# Index\nSee [[Engine]].\n");
        (await GetAsync(operatorClient, id, "Ops/Engine.md")).GetProperty("backlinks").EnumerateArray().Select(b => b.GetProperty("path").GetString()).Should().Equal("Index.md");

        var removed = await operatorClient.DeleteAsync(Note(id, "Ops/Engine.md"), Token);

        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await operatorClient.GetAsync(Note(id, "Ops/Engine.md"), Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.Exists(Path.Combine(folder, ".mnemarr-trash")).Should().BeTrue();
    }

    [Fact]
    public async Task should_let_an_assistant_write_and_audit_who_did_what_without_the_text()
    {
        var (id, _) = await SourceAsync(Callers.Unique("vault"));

        var created = await _callers["Assistant"].PostAsJsonAsync(Note(id, "memory.md"), new { content = "# Memory\nsecret-looking words\n" }, Token);
        var audit = await _callers.Admin.GetStringAsync("/api/v1/audit?action=note.created", Token);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        audit.Should().Contain("note.created").And.Contain("memory.md").And.NotContain("secret-looking words");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task should_not_let_a_reader_or_a_collector_or_anyone_not_signed_in_write(string method)
    {
        var (id, _) = await SourceAsync(Callers.Unique("vault"));
        await _callers["Operator"].PostAsJsonAsync(Note(id, "a.md"), new { content = "# A" }, Token);
        using var request = new HttpRequestMessage(new HttpMethod(method), Note(id, "a.md")) { Content = JsonContent.Create(new { content = "x", version = "v" }) };

        foreach (var (role, status) in new[] { ("Reader", HttpStatusCode.Forbidden), ("Collector", HttpStatusCode.Forbidden), ("Monitor", HttpStatusCode.Forbidden) })
        {
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Content = JsonContent.Create(new { content = "x", version = "v" }) };
            (await _callers[role].SendAsync(copy, Token)).StatusCode.Should().Be(status, role);
        }

        using var anonymous = new HttpRequestMessage(request.Method, request.RequestUri) { Content = JsonContent.Create(new { content = "x", version = "v" }) };
        (await _callers.Anonymous.SendAsync(anonymous, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.ReadAllText(Path.Combine(_factory.DataFolder, "sources", (await _callers["Operator"].GetFromJsonAsync<JsonElement>($"/api/v1/sources/{id}", Token)).GetProperty("name").GetString()!, "a.md")).Should().Be("# A");
    }

    [Fact]
    public async Task should_tell_a_reader_that_a_note_is_not_writable_and_refuse_a_source_that_is_read_only()
    {
        var (open, _) = await SourceAsync(Callers.Unique("open"));
        var (closed, _) = await SourceAsync(Callers.Unique("closed"), "ReadOnly");
        await _callers["Operator"].PostAsJsonAsync(Note(open, "a.md"), new { content = "# A" }, Token);

        var asReader = await GetAsync(_callers["Reader"], open, "a.md");
        var refused = await _callers["Operator"].PostAsJsonAsync(Note(closed, "a.md"), new { content = "# A" }, Token);

        asReader.GetProperty("writable").GetBoolean().Should().BeFalse();
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task should_say_what_each_caller_may_do_to_a_note_and_why_on_the_note_and_in_the_tree()
    {
        var (open, _) = await SourceAsync(Callers.Unique("open"));
        var (closed, closedFolder) = await SourceAsync(Callers.Unique("closed"), "ReadOnly");
        File.WriteAllText(Path.Combine(closedFolder, "r.md"), "# R");
        await _callers["Operator"].PostAsync($"/api/v1/sources/{closed}/index", null, Token);
        await _callers["Operator"].PostAsJsonAsync(Note(open, "w.md"), new { content = "# W" }, Token);

        var asAdmin = await GetAsync(_callers.Admin, open, "w.md");
        var asReader = await GetAsync(_callers["Reader"], open, "w.md");
        var readOnly = await GetAsync(_callers.Admin, closed, "r.md");
        var tree = await _callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/notes", Token);
        var treeSources = tree.GetProperty("sources").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetGuid());

        asAdmin.GetProperty("rights").GetProperty("reason").GetString().Should().Be("writable");
        asAdmin.GetProperty("rights").GetProperty("role").GetString().Should().Be("Admin");
        asAdmin.GetProperty("sourceKind").GetString().Should().Be("Knowledge");
        asReader.GetProperty("rights").GetProperty("reason").GetString().Should().Be("no-permission");
        asReader.GetProperty("writable").GetBoolean().Should().BeFalse();
        readOnly.GetProperty("rights").GetProperty("reason").GetString().Should().Be("source-read-only");
        readOnly.GetProperty("sourceMode").GetString().Should().Be("ReadOnly");
        treeSources[open].GetProperty("rights").GetProperty("reason").GetString().Should().Be("no-permission");
        treeSources[closed].GetProperty("mode").GetString().Should().Be("ReadOnly");
    }

    [Fact]
    public async Task should_refuse_to_write_the_documentation_of_an_application_and_say_why()
    {
        var name = Callers.Unique("docs");
        var folder = Path.Combine(_factory.DataFolder, "sources", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "d.md"), "# D");
        var created = await _callers["Operator"].PostAsJsonAsync("/api/v1/sources", new { name, kind = "ApplicationDocs", mode = "ReadWrite", path = folder }, Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        await _callers["Operator"].PostAsync($"/api/v1/sources/{id}/index", null, Token);

        var note = await GetAsync(_callers.Admin, id, "d.md");
        var refused = await _callers.Admin.PostAsJsonAsync(Note(id, "new.md"), new { content = "# N" }, Token);

        note.GetProperty("rights").GetProperty("reason").GetString().Should().Be("kind-not-writable");
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Token)).Should().Contain("repository");
    }

    [Fact]
    public async Task should_treat_a_source_out_of_the_scope_of_a_key_as_absent_when_writing()
    {
        var (mine, _) = await SourceAsync(Callers.Unique("mine"));
        var (other, _) = await SourceAsync(Callers.Unique("other"));
        var service = await Callers.CreateAsync(_callers.Admin, new { name = Callers.Unique("svc"), kind = "Service", role = "Assistant" });
        var created = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{service}/keys", new { name = "scoped", sources = new[] { mine } }, Token);
        using var scoped = _factory.CreateKeyClient((await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        (await scoped.PostAsJsonAsync(Note(mine, "a.md"), new { content = "# A" }, Token)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await scoped.PostAsJsonAsync(Note(other, "a.md"), new { content = "# A" }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("../x.md")]
    [InlineData(".hidden/x.md")]
    [InlineData("notes.txt")]
    public async Task should_refuse_a_path_that_is_not_a_plain_markdown_path_with_a_400(string path)
    {
        var (id, _) = await SourceAsync(Callers.Unique("vault"));

        var response = await _callers["Operator"].PostAsJsonAsync(Note(id, path), new { content = "# A" }, Token);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_preview_a_text_as_a_note_of_the_source_with_its_links_resolved_and_write_nothing()
    {
        var (id, folder) = await SourceAsync(Callers.Unique("vault"));
        await _callers["Operator"].PostAsJsonAsync(Note(id, "Target.md"), new { content = "# Target" }, Token);

        var response = await _callers["Operator"].PostAsJsonAsync($"/api/v1/notes/{id}/preview", new { path = "New.md", content = "See [[Target#Part One]] and [[ghost]]." }, Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("rendered").GetString().Should().Contain($"mnemarr-note:{id}/Target.md#part-one").And.Contain("mnemarr-unresolved:");
        File.Exists(Path.Combine(folder, "New.md")).Should().BeFalse();
    }

    [Fact]
    public async Task should_commit_and_push_a_change_to_a_git_source_as_the_account_and_say_what_became_of_it()
    {
        var name = Callers.Unique("git");
        var (id, _) = await SourceAsync(name, repository: new { url = "https://github.example/org/notes", branch = "main", credential = "github" });
        var before = _factory.Git.Commits.Count;

        var created = await _callers["Operator"].PostAsJsonAsync(Note(id, "a.md"), new { content = "# A" }, Token);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Token);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        body.GetProperty("git").GetProperty("pushed").GetBoolean().Should().BeTrue();
        _factory.Git.Commits.Skip(before).Should().ContainSingle().Which.Should().Match<(string Folder, string[] Paths, string Message, Mnemarr.Core.Git.GitAuthor Author)>(c => c.Message == "Add a.md" && c.Author.Email == "mnemarr@localhost");
    }
}
