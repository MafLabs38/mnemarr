using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mnemarr.Core.Git;

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
        var name = Unique("courses");
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
    public async Task should_refuse_a_source_that_does_not_follow_the_rules_with_a_400_problem_document(string wrong)
    {
        var name = Unique("rules");
        var path = wrong switch
        {
            "outside" => Path.GetTempPath(),
            _ => Folder(name),
        };

        var response = await _callers["Operator"].PostAsJsonAsync(
            "/api/v1/sources", new { name, kind = wrong == "kind" ? "Diary" : "Knowledge", mode = wrong == "mode" ? "Append" : "ReadOnly", path }, Token);

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
    public async Task should_not_let_a_key_limited_to_some_sources_declare_a_source_or_repoint_its_own_at_another_folder()
    {
        var mine = Unique("mine");
        var other = Unique("other");
        var mineFolder = Folder(mine, ("a.md", "# A\nmine"));
        var otherFolder = Folder(other, ("b.md", "# B\nother"));
        var mineId = (await DeclareAsync(_callers.Admin, mine, mineFolder)).GetProperty("id").GetGuid();
        var otherId = (await DeclareAsync(_callers.Admin, other, otherFolder)).GetProperty("id").GetGuid();
        var service = await Callers.CreateAsync(_callers.Admin, new { name = Unique("svc"), kind = "Service", role = "Operator" });
        var created = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{service}/keys", new { name = "scoped", sources = new[] { mineId } }, Token);
        using var scoped = _factory.CreateKeyClient((await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        var declare = await scoped.PostAsJsonAsync("/api/v1/sources", new { name = Unique("new"), kind = "Knowledge", mode = "ReadOnly", path = Folder(Unique("new")) }, Token);
        var toOther = await scoped.PutAsJsonAsync($"/api/v1/sources/{mineId}", new { name = mine, mode = "ReadOnly", path = otherFolder }, Token);
        var toRoot = await scoped.PutAsJsonAsync($"/api/v1/sources/{mineId}", new { name = mine, mode = "ReadOnly", path = Path.Combine(_factory.DataFolder, "sources") }, Token);
        var outOfScope = await scoped.PutAsJsonAsync($"/api/v1/sources/{otherId}", new { name = other, mode = "ReadOnly", path = otherFolder }, Token);
        var mode = await scoped.PutAsJsonAsync($"/api/v1/sources/{mineId}", new { name = mine, mode = "ReadWrite", path = mineFolder }, Token);

        declare.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        toOther.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        toRoot.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        outOfScope.StatusCode.Should().Be(HttpStatusCode.NotFound);
        mode.StatusCode.Should().Be(HttpStatusCode.OK);
        (await scoped.DeleteAsync($"/api/v1/sources/{mineId}", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "forgetting its own source would leave the key with nothing to match");
        (await _callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/sources/{mineId}", Token)).GetProperty("path").GetString().Should().Be(mineFolder);
    }

    [Fact]
    public async Task should_count_in_the_kinds_only_the_sources_a_key_is_limited_to()
    {
        await using var factory = new MnemarrFactory();
        using var admin = await factory.SignedInClientAsync();
        string Make(string name) => Directory.CreateDirectory(Path.Combine(factory.DataFolder, "sources", name)).FullName;
        var memory = await admin.PostAsJsonAsync("/api/v1/sources", new { name = "memory", kind = "Memory", mode = "ReadOnly", path = Make("m") }, Token);
        var notes = await admin.PostAsJsonAsync("/api/v1/sources", new { name = "notes", kind = "Knowledge", mode = "ReadOnly", path = Make("k") }, Token);
        var notesId = (await notes.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        memory.StatusCode.Should().Be(HttpStatusCode.Created);
        var service = await Callers.CreateAsync(admin, new { name = Unique("svc"), kind = "Service", role = "Operator" });
        var key = await admin.PostAsJsonAsync($"/api/v1/accounts/{service}/keys", new { name = "scoped", sources = new[] { notesId } }, Token);
        using var scoped = factory.CreateKeyClient((await key.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        var kinds = await scoped.GetFromJsonAsync<JsonElement>("/api/v1/sources/kinds", Token);

        kinds.EnumerateArray().Single(k => k.GetProperty("kind").GetString() == "Memory").GetProperty("count").GetInt32().Should().Be(0);
        kinds.EnumerateArray().Single(k => k.GetProperty("kind").GetString() == "Knowledge").GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task should_show_on_the_source_why_its_last_scan_failed_and_cut_every_note_again_on_a_rebuild()
    {
        var name = Unique("scan");
        var folder = Folder(name, ("a.md", "# A\nrebuildword"));
        var id = (await DeclareAsync(_callers.Admin, name, folder)).GetProperty("id").GetGuid();
        var first = await (await _callers.Admin.PostAsync($"/api/v1/sources/{id}/index", null, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var again = await (await _callers.Admin.PostAsync($"/api/v1/sources/{id}/index?rebuild=true", null, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var healthy = await _callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/sources/{id}", Token);

        Directory.Delete(folder, recursive: true);
        var failed = await _callers.Admin.PostAsync($"/api/v1/sources/{id}/index", null, Token);
        var shown = await _callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/sources/{id}", Token);

        first.GetProperty("added").GetInt32().Should().Be(1);
        again.GetProperty("updated").GetInt32().Should().Be(1);
        healthy.GetProperty("lastError").ValueKind.Should().Be(JsonValueKind.Null);
        healthy.GetProperty("lastScanAt").ValueKind.Should().Be(JsonValueKind.String);
        failed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        shown.GetProperty("lastError").GetString().Should().Be("folder-missing");
        shown.GetProperty("notes").GetInt32().Should().Be(1, "the notes of the last good pass are kept and still searchable");
        (await _callers.Admin.DeleteAsync($"/api/v1/sources/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
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
        memory.GetProperty("defaultMode").GetString().Should().Be("ReadOnly", "writes are off unless a source asks for them");
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

    private async Task<JsonElement> DeclareGitAsync(HttpClient client, string name, string url = "https://github.example/org/notes", string? credential = "github", string? branch = "main")
    {
        var path = Path.Combine(_factory.DataFolder, "sources", name);
        var response = await client.PostAsJsonAsync("/api/v1/sources", new { name, kind = "Knowledge", mode = "ReadOnly", path, repository = new { url, branch, credential } }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    [Fact]
    public async Task should_declare_a_git_source_show_its_repository_and_never_a_secret()
    {
        var name = Unique("git");

        var source = await DeclareGitAsync(_callers["Operator"], name, "https://GitHub.example/org/notes/");
        var text = await _callers["Operator"].GetStringAsync($"/api/v1/sources/{source.GetProperty("id").GetGuid()}", Token);

        var repository = source.GetProperty("repository");
        repository.GetProperty("url").GetString().Should().Be("https://github.example/org/notes");
        repository.GetProperty("branch").GetString().Should().Be("main");
        repository.GetProperty("credential").GetString().Should().Be("github");
        source.GetProperty("lastSyncAt").ValueKind.Should().Be(JsonValueKind.Null);
        text.Should().Contain("\"repository\"").And.NotContain("secret-token-do-not-leak");
    }

    [Theory]
    [InlineData("http://github.example/org/notes", "github", "https")]
    [InlineData("https://user:pw@github.example/org/notes", "github", "never written")]
    [InlineData("https://elsewhere.example/org/notes", null, "does not allow")]
    [InlineData("https://github.example/org/notes", "nope", "no credential")]
    public async Task should_refuse_a_repository_that_the_policy_does_not_allow(string url, string? credential, string reason)
    {
        var path = Path.Combine(_factory.DataFolder, "sources", Unique("bad"));

        var response = await _callers["Operator"].PostAsJsonAsync("/api/v1/sources", new { name = Unique("bad"), kind = "Knowledge", mode = "ReadOnly", path, repository = new { url, credential } }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(Token);
        body.Should().Contain(reason).And.NotContain("secret-token-do-not-leak").And.NotContain("pw@");
    }

    [Fact]
    public async Task should_sync_a_git_source_now_and_show_how_it_ended()
    {
        var source = await DeclareGitAsync(_callers["Operator"], Unique("git"));
        var id = source.GetProperty("id").GetGuid();
        _factory.Git.Result = _ => new GitSyncResult(GitSyncAction.Updated, "def4567");

        var response = await _callers["Operator"].PostAsync($"/api/v1/sources/{id}/sync", null, Token);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var after = await _callers["Operator"].GetFromJsonAsync<JsonElement>($"/api/v1/sources/{id}", Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        report.GetProperty("action").GetString().Should().Be("Updated");
        report.GetProperty("head").GetString().Should().Be("def4567");
        after.GetProperty("head").GetString().Should().Be("def4567");
        after.GetProperty("lastSyncAt").ValueKind.Should().Be(JsonValueKind.String);
        after.GetProperty("lastSyncError").ValueKind.Should().Be(JsonValueKind.Null);
        _factory.Git.Calls.Should().Contain(call => call.Folder.EndsWith(source.GetProperty("name").GetString()!, StringComparison.Ordinal) && call.Secret != null && call.Secret.Token == "secret-token-do-not-leak");
    }

    [Fact]
    public async Task should_answer_a_conflict_in_fixed_words_when_a_sync_fails_and_degrade_readiness_until_it_works_again()
    {
        await using var factory = new MnemarrFactory();
        using var admin = await factory.SignedInClientAsync();
        var memory = Path.Combine(factory.DataFolder, "sources", "memory");
        Directory.CreateDirectory(memory);
        await admin.PostAsJsonAsync("/api/v1/sources", new { name = "memory", kind = "Memory", mode = "ReadWrite", path = memory }, Token);
        var path = Path.Combine(factory.DataFolder, "sources", "notes");
        var created = await admin.PostAsJsonAsync("/api/v1/sources", new { name = "notes", kind = "Knowledge", mode = "ReadOnly", path, repository = new { url = "https://github.example/org/notes", credential = "github" } }, Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        factory.Git.Result = _ => throw new GitSyncException(GitFailure.AuthFailed, "401 for https://github.example/org/notes with secret-token-do-not-leak");

        var failure = await admin.PostAsync($"/api/v1/sources/{id}/sync", null, Token);
        var body = await failure.Content.ReadAsStringAsync(Token);
        var shown = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sources/{id}", Token);
        var degraded = await (await admin.GetAsync("/health/ready", Token)).Content.ReadAsStringAsync(Token);
        factory.Git.Result = _ => new GitSyncResult(GitSyncAction.UpToDate, "abc123");
        await admin.PostAsync($"/api/v1/sources/{id}/sync", null, Token);
        var recovered = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sources/{id}", Token);
        var ready = await (await admin.GetAsync("/health/ready", Token)).Content.ReadAsStringAsync(Token);

        failure.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.Should().Contain("The host refused the credential").And.NotContain("secret-token-do-not-leak").And.NotContain("github.example");
        shown.GetProperty("lastSyncError").GetString().Should().Be("auth-failed");
        degraded.Should().Be("Degraded");
        recovered.GetProperty("lastSyncError").ValueKind.Should().Be(JsonValueKind.Null);
        ready.Should().Be("Healthy");
    }

    [Fact]
    public async Task should_refuse_to_sync_a_source_that_is_a_folder_and_one_that_is_not_there()
    {
        var name = Unique("plain");
        var plain = await DeclareAsync(_callers["Operator"], name, Folder(name));

        var folder = await _callers["Operator"].PostAsync($"/api/v1/sources/{plain.GetProperty("id").GetGuid()}/sync", null, Token);
        var missing = await _callers["Operator"].PostAsync($"/api/v1/sources/{Guid.NewGuid()}/sync", null, Token);

        folder.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_let_only_who_manages_sources_sync_and_only_the_sources_of_a_limited_key()
    {
        var mine = (await DeclareGitAsync(_callers.Admin, Unique("mine"))).GetProperty("id").GetGuid();
        var other = (await DeclareGitAsync(_callers.Admin, Unique("other"))).GetProperty("id").GetGuid();
        var service = await Callers.CreateAsync(_callers.Admin, new { name = Unique("svc"), kind = "Service", role = "Operator" });
        var created = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{service}/keys", new { name = "scoped", sources = new[] { mine } }, Token);
        using var scoped = _factory.CreateKeyClient((await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        (await _callers["Reader"].PostAsync($"/api/v1/sources/{mine}/sync", null, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _callers.Anonymous.PostAsync($"/api/v1/sources/{mine}/sync", null, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await scoped.PostAsync($"/api/v1/sources/{mine}/sync", null, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await scoped.PostAsync($"/api/v1/sources/{other}/sync", null, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_change_the_repository_of_a_source_and_turn_it_back_into_a_folder()
    {
        var name = Unique("git");
        var source = await DeclareGitAsync(_callers["Operator"], name);
        var id = source.GetProperty("id").GetGuid();
        var path = Path.Combine(_factory.DataFolder, "sources", name);

        var moved = await (await _callers["Operator"].PutAsJsonAsync($"/api/v1/sources/{id}", new { name, mode = "ReadOnly", path, repository = new { url = "https://github.example/org/other", branch = "dev", credential = "github" } }, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        Directory.CreateDirectory(path);
        var plain = await (await _callers["Operator"].PutAsJsonAsync($"/api/v1/sources/{id}", new { name, mode = "ReadOnly", path }, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);

        moved.GetProperty("repository").GetProperty("branch").GetString().Should().Be("dev");
        plain.GetProperty("repository").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
