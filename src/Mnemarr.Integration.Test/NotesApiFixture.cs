using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The notes reader over HTTP: the tree, a note with its links, the images, and what a key limited to some sources cannot see.</summary>
public sealed class NotesApiFixture : IClassFixture<MnemarrFactory>, IAsyncLifetime
{
    private readonly MnemarrFactory _factory;
    private Callers _callers = null!;

    public NotesApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _callers = await Callers.ForAsync(_factory);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Guid> IndexedSourceAsync(string name, params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(_factory.DataFolder, "sources", name);
        Directory.CreateDirectory(folder);
        foreach (var (path, text) in files)
        {
            var file = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        var created = await _callers["Operator"].PostAsJsonAsync("/api/v1/sources", new { name, kind = "Knowledge", mode = "ReadOnly", path = folder }, Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        (await _callers["Operator"].PostAsync($"/api/v1/sources/{id}/index", null, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    [Fact]
    public async Task should_list_read_and_follow_the_notes_of_a_source_as_a_reader()
    {
        var name = Callers.Unique("vault");
        var other = Callers.Unique("other");
        var id = await IndexedSourceAsync(name, ("devops/docker.md", "---\ntags: [infra]\n---\n# Docker\nSee [[kubernetes]] and [[ghost]] ![[plan.png]]"), ("devops/kubernetes.md", "# K8s\n[[docker]]"));
        await IndexedSourceAsync(other, ("kubernetes.md", "# Elsewhere"));
        await File.WriteAllTextAsync(Path.Combine(_factory.DataFolder, "sources", name, "devops", "plan.png"), "png", Token);
        var reader = _callers["Reader"];

        var tree = (await reader.GetFromJsonAsync<JsonElement>($"/api/v1/notes?source={id}", Token)).EnumerateArray().Select(n => n.GetProperty("path").GetString()).ToList();
        var note = await reader.GetFromJsonAsync<JsonElement>($"/api/v1/notes/{id}/devops/docker.md", Token);
        var tagged = await reader.GetFromJsonAsync<JsonElement>("/api/v1/notes?tag=infra", Token);
        var tags = await reader.GetFromJsonAsync<JsonElement>("/api/v1/notes/tags", Token);

        tree.Should().Equal("devops/docker.md", "devops/kubernetes.md");
        note.GetProperty("markdown").GetString().Should().StartWith("# Docker").And.NotContain("tags:");
        note.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("infra");
        var links = note.GetProperty("links").EnumerateArray().ToDictionary(l => l.GetProperty("target").GetString()!, l => l.GetProperty("note"));
        links["kubernetes"].GetProperty("path").GetString().Should().Be("devops/kubernetes.md", "the same source comes first");
        links["ghost"].ValueKind.Should().Be(JsonValueKind.Null);
        note.GetProperty("backlinks").EnumerateArray().Select(b => b.GetProperty("path").GetString()).Should().Equal("devops/kubernetes.md");
        note.GetProperty("embeds").GetProperty("plan.png").GetString().Should().Be("devops/plan.png");
        tagged.EnumerateArray().Select(n => n.GetProperty("path").GetString()).Should().Contain("devops/docker.md");
        tags.EnumerateArray().Should().Contain(t => t.GetProperty("tag").GetString() == "infra");
    }

    [Fact]
    public async Task should_serve_an_image_of_the_source_with_headers_that_keep_it_from_running_and_refuse_anything_else()
    {
        var id = await IndexedSourceAsync(Callers.Unique("vault"), ("a.md", "# A"));
        var folder = Path.Combine(_factory.DataFolder, "sources", (await _callers["Operator"].GetFromJsonAsync<JsonElement>($"/api/v1/sources/{id}", Token)).GetProperty("name").GetString()!);
        await File.WriteAllTextAsync(Path.Combine(folder, "p.png"), "png", Token);
        await File.WriteAllTextAsync(Path.Combine(folder, "p.svg"), "<svg onload=alert(1)/>", Token);

        var image = await _callers["Reader"].GetAsync($"/api/v1/files/{id}/p.png", Token);
        var svg = await _callers["Reader"].GetAsync($"/api/v1/files/{id}/p.svg", Token);
        var note = await _callers["Reader"].GetAsync($"/api/v1/files/{id}/a.md", Token);
        var up = await _callers["Reader"].GetAsync($"/api/v1/files/{id}/..%2F..%2Fsecret.png", Token);

        image.StatusCode.Should().Be(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        image.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        image.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'self'", "the strict policy of the whole application applies to an image opened on its own");
        svg.StatusCode.Should().Be(HttpStatusCode.NotFound);
        note.StatusCode.Should().Be(HttpStatusCode.NotFound);
        up.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_show_a_key_limited_to_some_sources_only_those_and_answer_404_for_the_others()
    {
        var mine = await IndexedSourceAsync(Callers.Unique("mine"), ("a.md", "# A\n[[b]] #shared"));
        var theirs = await IndexedSourceAsync(Callers.Unique("theirs"), ("b.md", "# B\n[[a]] #hidden"));
        var service = await Callers.CreateAsync(_callers.Admin, new { name = Callers.Unique("svc"), kind = "Service", role = "Reader" });
        var created = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{service}/keys", new { name = "scoped", sources = new[] { mine } }, Token);
        using var scoped = _factory.CreateKeyClient((await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        var tree = await scoped.GetFromJsonAsync<JsonElement>("/api/v1/notes", Token);
        var note = await scoped.GetFromJsonAsync<JsonElement>($"/api/v1/notes/{mine}/a.md", Token);
        var tags = await scoped.GetFromJsonAsync<JsonElement>("/api/v1/notes/tags", Token);

        tree.EnumerateArray().Select(n => n.GetProperty("path").GetString()).Should().Equal("a.md");
        note.GetProperty("links").EnumerateArray().Single().GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null, "the note exists, but not for this key");
        note.GetProperty("backlinks").GetArrayLength().Should().Be(0);
        tags.EnumerateArray().Select(t => t.GetProperty("tag").GetString()).Should().NotContain("hidden");
        (await scoped.GetAsync($"/api/v1/notes/{theirs}/b.md", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await scoped.GetAsync($"/api/v1/files/{theirs}/p.png", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
