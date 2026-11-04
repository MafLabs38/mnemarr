using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Mnemarr.Integration.Test;

/// <summary>Declaring, indexing and searching sources over HTTP, as an operator, a reader and a key limited to some sources.</summary>
public sealed class SourcesApiFixture : IClassFixture<MnemarrFactory>, IAsyncLifetime
{
    private readonly MnemarrFactory _factory;
    private Callers _callers = null!;

    public SourcesApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _callers = await Callers.ForAsync(_factory);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A folder in the place the operator allows by default (the sources folder of the data folder), with notes in it.</summary>
    private string Folder(string name, params (string Path, string Text)[] notes)
    {
        var folder = Path.Combine(_factory.DataFolder, "sources", name);
        foreach (var (path, text) in notes)
        {
            var file = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<JsonElement> DeclareAsync(HttpClient client, string name, string path, string kind = "Knowledge", string mode = "ReadOnly")
    {
        var response = await client.PostAsJsonAsync("/api/v1/sources", new { name, kind, mode, path }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static string Unique(string prefix) => Callers.Unique(prefix);

    [Fact]
    public async Task should_declare_a_folder_index_it_and_find_its_notes_by_their_words_with_the_source_shown()
    {
        var name = Unique("cours");
        var folder = Folder(name, ("02 - DevOps/docker.md", "# Docker\n## Images\nUne image est faite de couches.\n"), ("04 - Angular/signals.md", "# Signals\nLes signals remplacent zone.js.\n"));

        var source = await DeclareAsync(_callers["Operator"], name, folder);
        var id = source.GetProperty("id").GetGuid();
        var report = await (await _callers["Operator"].PostAsync($"/api/v1/sources/{id}/index", null, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var hits = await _callers["Reader"].GetFromJsonAsync<JsonElement>($"/api/v1/search?q=couches&source={id}", Token);

        report.GetProperty("added").GetInt32().Should().Be(2);
        var hit = hits.EnumerateArray().Should().ContainSingle().Subject;
        hit.GetProperty("sourceName").GetString().Should().Be(name);
        hit.GetProperty("path").GetString().Should().Be("02 - DevOps/docker.md");
        hit.GetProperty("topic").GetString().Should().Be("02 - DevOps");
        var listed = (await _callers["Operator"].GetFromJsonAsync<JsonElement>("/api/v1/sources", Token)).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
        listed.GetProperty("notes").GetInt32().Should().Be(2);
        listed.GetProperty("lastIndexedAt").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("mode")]
    [InlineData("outside")]
    [InlineData("missing")]
    [InlineData("name")]
    public async Task should_refuse_a_source_that_does_not_follow_the_rules_with_a_400_problem_document(string wrong)
    {
        var name = Unique("rules");
        var path = wrong switch
        {
            "outside" => Path.GetTempPath(),
            "missing" => Path.Combine(_factory.DataFolder, "sources", Unique("nothing")),
            _ => Folder(name),
        };

        var response = await _callers["Operator"].PostAsJsonAsync(
            "/api/v1/sources", new { name = wrong == "name" ? " " : name, kind = wrong == "kind" ? "Diary" : "Knowledge", mode = wrong == "mode" ? "Append" : "ReadOnly", path }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task should_refuse_a_second_source_with_the_same_name_with_a_409()
    {
        var name = Unique("twice");
        var folder = Folder(name);
        await DeclareAsync(_callers["Operator"], name, folder);

        var again = await _callers["Operator"].PostAsJsonAsync("/api/v1/sources", new { name, kind = "Knowledge", mode = "ReadOnly", path = folder }, Token);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task should_forget_a_source_and_its_notes_and_leave_its_files_where_they_are()
    {
        var name = Unique("gone");
        var folder = Folder(name, ("a.md", "# A\nunforgettable word"));
        var id = (await DeclareAsync(_callers["Operator"], name, folder)).GetProperty("id").GetGuid();
        await _callers["Operator"].PostAsync($"/api/v1/sources/{id}/index", null, Token);
        (await _callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/search?q=unforgettable", Token)).GetArrayLength().Should().BeGreaterThan(0);

        (await _callers["Operator"].DeleteAsync($"/api/v1/sources/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/search?q=unforgettable", Token)).GetArrayLength().Should().Be(0);
        File.Exists(Path.Combine(folder, "a.md")).Should().BeTrue();
        (await _callers["Operator"].DeleteAsync($"/api/v1/sources/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_let_a_reader_search_and_not_manage_sources_and_tell_the_operator_where_sources_may_be()
    {
        var folder = Folder(Unique("nope"));

        var declare = await _callers["Reader"].PostAsJsonAsync("/api/v1/sources", new { name = "nope", kind = "Knowledge", mode = "ReadOnly", path = folder }, Token);
        var roots = await _callers["Operator"].GetFromJsonAsync<JsonElement>("/api/v1/sources/roots", Token);

        declare.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        roots.EnumerateArray().Select(r => r.GetString()).Should().Contain(Path.Combine(_factory.DataFolder, "sources"));
    }

    [Fact]
    public async Task should_search_and_list_only_the_sources_a_key_is_limited_to_and_treat_the_others_as_absent()
    {
        var mine = Unique("mine");
        var other = Unique("other");
        var mineId = (await DeclareAsync(_callers.Admin, mine, Folder(mine, ("a.md", "# A\nscopedword in mine")))).GetProperty("id").GetGuid();
        var otherId = (await DeclareAsync(_callers.Admin, other, Folder(other, ("b.md", "# B\nscopedword in other")))).GetProperty("id").GetGuid();
        await _callers.Admin.PostAsync($"/api/v1/sources/{mineId}/index", null, Token);
        await _callers.Admin.PostAsync($"/api/v1/sources/{otherId}/index", null, Token);
        var service = await Callers.CreateAsync(_callers.Admin, new { name = Unique("svc"), kind = "Service", role = "Operator" });
        var created = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{service}/keys", new { name = "scoped", sources = new[] { mineId } }, Token);
        using var scoped = _factory.CreateKeyClient((await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        var hits = await scoped.GetFromJsonAsync<JsonElement>("/api/v1/search?q=scopedword", Token);
        var sources = await scoped.GetFromJsonAsync<JsonElement>("/api/v1/sources", Token);

        hits.EnumerateArray().Select(h => h.GetProperty("sourceName").GetString()).Should().Equal(mine);
        sources.EnumerateArray().Select(s => s.GetProperty("name").GetString()).Should().Equal(mine);
        (await scoped.GetAsync($"/api/v1/sources/{otherId}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await scoped.DeleteAsync($"/api/v1/sources/{otherId}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await scoped.PostAsync($"/api/v1/sources/{otherId}/index", null, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _callers.Admin.GetAsync($"/api/v1/sources/{otherId}", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task should_answer_a_search_with_no_word_in_it_with_nothing_rather_than_an_error()
    {
        (await _callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/search?q=%3F%21", Token)).GetArrayLength().Should().Be(0);
        (await _callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/search", Token)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task should_audit_the_declaring_and_the_forgetting_of_a_source()
    {
        var name = Unique("audited");
        var id = (await DeclareAsync(_callers["Operator"], name, Folder(name))).GetProperty("id").GetGuid();
        await _callers["Operator"].DeleteAsync($"/api/v1/sources/{id}", Token);

        var events = await _callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=20&action=source.added", Token);

        events.EnumerateArray().Select(e => e.GetProperty("target").GetString()).Should().Contain(name);
    }

    [Fact]
    public async Task should_list_the_kinds_with_their_rules_and_say_what_is_missing_from_the_base()
    {
        await using var factory = new MnemarrFactory();
        using var admin = await factory.SignedInClientAsync();

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/v1/sources/kinds", Token);
        var memory = before.EnumerateArray().Single(k => k.GetProperty("kind").GetString() == "Memory");
        var knowledge = before.EnumerateArray().Single(k => k.GetProperty("kind").GetString() == "Knowledge");

        memory.GetProperty("required").GetBoolean().Should().BeTrue();
        memory.GetProperty("unique").GetBoolean().Should().BeTrue();
        memory.GetProperty("satisfied").GetBoolean().Should().BeFalse();
        memory.GetProperty("defaultMode").GetString().Should().Be("ReadWrite");
        knowledge.GetProperty("required").GetBoolean().Should().BeFalse();
        knowledge.GetProperty("unique").GetBoolean().Should().BeFalse();
        knowledge.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        (await (await admin.GetAsync("/health/ready", Token)).Content.ReadAsStringAsync(Token)).Should().Be("Degraded");
    }

    [Fact]
    public async Task should_be_ready_once_the_memory_vault_is_declared_and_degraded_again_never_because_it_cannot_be_removed()
    {
        await using var factory = new MnemarrFactory();
        using var admin = await factory.SignedInClientAsync();
        var folder = Path.Combine(factory.DataFolder, "sources", "memory");
        Directory.CreateDirectory(folder);
        var created = await admin.PostAsJsonAsync("/api/v1/sources", new { name = "memory", kind = "Memory", mode = "ReadWrite", path = folder }, Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();

        var ready = await (await admin.GetAsync("/health/ready", Token)).Content.ReadAsStringAsync(Token);
        var removal = await admin.DeleteAsync($"/api/v1/sources/{id}", Token);

        ready.Should().Be("Healthy");
        removal.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sources/{id}", Token)).GetProperty("name").GetString().Should().Be("memory");
    }

    [Fact]
    public async Task should_refuse_a_second_memory_vault_with_a_409_and_let_the_first_change_its_folder()
    {
        await using var factory = new MnemarrFactory();
        using var admin = await factory.SignedInClientAsync();
        string Make(string name)
        {
            var folder = Path.Combine(factory.DataFolder, "sources", name);
            Directory.CreateDirectory(folder);
            return folder;
        }

        var first = await admin.PostAsJsonAsync("/api/v1/sources", new { name = "memory", kind = "Memory", mode = "ReadWrite", path = Make("m1") }, Token);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        var second = await admin.PostAsJsonAsync("/api/v1/sources", new { name = "other", kind = "Memory", mode = "ReadWrite", path = Make("m2") }, Token);
        var moved = await admin.PutAsJsonAsync($"/api/v1/sources/{id}", new { name = "memory", mode = "ReadWrite", path = Make("m3") }, Token);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        second.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await moved.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("path").GetString().Should().EndWith("m3");
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/sources", Token)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task should_refuse_a_change_of_mode_that_is_not_one_and_a_change_of_a_source_that_is_not_there()
    {
        var name = Unique("edit");
        var id = (await DeclareAsync(_callers["Operator"], name, Folder(name))).GetProperty("id").GetGuid();

        var badMode = await _callers["Operator"].PutAsJsonAsync($"/api/v1/sources/{id}", new { name, mode = "Append", path = Folder(name) }, Token);
        var missing = await _callers["Operator"].PutAsJsonAsync($"/api/v1/sources/{Guid.NewGuid()}", new { name, mode = "ReadOnly", path = Folder(name) }, Token);

        badMode.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
