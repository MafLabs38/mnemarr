using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>Configuration as code over HTTP: a YAML document that is read, previewed and applied, and the sources it owns.</summary>
public sealed class ConfigApiFixture : IClassFixture<MnemarrFactory>, IAsyncLifetime
{
    private readonly MnemarrFactory _factory;
    private Callers _callers = null!;

    public ConfigApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _callers = await Callers.ForAsync(_factory);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Folder(string name)
    {
        var folder = Path.Combine(_factory.DataFolder, "sources", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static StringContent Yaml(string text, string type = "application/yaml") => new(text, Encoding.UTF8, new MediaTypeHeaderValue(type));

    private async Task<JsonElement> SourcesAsync(HttpClient? client = null) =>
        await (client ?? _callers.Admin).GetFromJsonAsync<JsonElement>("/api/v1/sources", Token);

    [Fact]
    public async Task should_apply_a_document_and_declare_its_sources_as_managed_by_configuration()
    {
        var name = Callers.Unique("cours");

        var response = await _callers["Operator"].PutAsync("/api/v1/config", Yaml($"knowledge:\n  - name: {name}\n    path: {Folder(name)}\n"), Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        plan.GetProperty("applied").GetBoolean().Should().BeTrue();
        plan.GetProperty("items").EnumerateArray().Should().ContainSingle().Which.GetProperty("action").GetString().Should().Be("Create");
        (await SourcesAsync()).EnumerateArray().Single(s => s.GetProperty("name").GetString() == name).GetProperty("managed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task should_say_what_a_document_would_do_without_doing_it()
    {
        var name = Callers.Unique("preview");

        var response = await _callers.Admin.PutAsync("/api/v1/config?dryRun=true", Yaml($"applicationDocs:\n  - name: {name}\n    path: {Folder(name)}\n"), Token);

        var plan = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        plan.GetProperty("applied").GetBoolean().Should().BeFalse();
        plan.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("name").GetString() == name);
        (await SourcesAsync()).EnumerateArray().Should().NotContain(s => s.GetProperty("name").GetString() == name);
    }

    [Fact]
    public async Task should_apply_none_of_a_document_with_an_error_and_answer_400_with_every_error()
    {
        var good = Callers.Unique("good");
        var document = $"knowledge:\n  - name: {good}\n    path: {Folder(good)}\n  - name: bad\n    path: /etc\n  - path: {Folder(good)}\n";

        var response = await _callers.Admin.PutAsync("/api/v1/config", Yaml(document), Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("errors").GetArrayLength().Should().Be(2);
        (await SourcesAsync()).EnumerateArray().Should().NotContain(s => s.GetProperty("name").GetString() == good);
    }

    [Theory]
    [InlineData("memry:\n  path: /x\n")]
    [InlineData("memory: &m\n  path: /x\n")]
    [InlineData("memory: [unterminated\n")]
    public async Task should_answer_a_document_that_is_not_one_with_a_400_problem(string document)
    {
        var response = await _callers.Admin.PutAsync("/api/v1/config", Yaml(document), Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task should_accept_plain_text_and_refuse_a_media_type_that_is_neither_yaml_nor_text()
    {
        var plain = await _callers.Admin.PutAsync("/api/v1/config?dryRun=true", Yaml("knowledge: []\n", "text/plain"), Token);
        var json = await _callers.Admin.PutAsync("/api/v1/config?dryRun=true", new StringContent("{}", Encoding.UTF8, "application/json"), Token);

        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        json.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task should_refuse_a_document_larger_than_the_limit_before_reading_it()
    {
        var response = await _callers.Admin.PutAsync("/api/v1/config", Yaml("# " + new string('x', 300 * 1024)), Token);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task should_refuse_to_change_or_forget_by_hand_what_a_document_owns_with_a_409_and_still_let_a_person_declare_others()
    {
        var owned = Callers.Unique("owned");
        await _callers.Admin.PutAsync("/api/v1/config", Yaml($"applicationDocs:\n  - name: {owned}\n    path: {Folder(owned)}\n"), Token);
        var id = (await SourcesAsync()).EnumerateArray().Single(s => s.GetProperty("name").GetString() == owned).GetProperty("id").GetGuid();
        var byHand = Callers.Unique("hand");

        var change = await _callers.Admin.PutAsJsonAsync($"/api/v1/sources/{id}", new { name = owned, mode = "ReadWrite", path = Folder(owned) }, Token);
        var forget = await _callers.Admin.DeleteAsync($"/api/v1/sources/{id}", Token);
        var declare = await _callers.Admin.PostAsJsonAsync("/api/v1/sources", new { name = byHand, kind = "Knowledge", mode = "ReadWrite", path = Folder(byHand) }, Token);

        change.StatusCode.Should().Be(HttpStatusCode.Conflict);
        forget.StatusCode.Should().Be(HttpStatusCode.Conflict);
        declare.StatusCode.Should().Be(HttpStatusCode.Created);
        (await (await _callers.Admin.PutAsync("/api/v1/config", Yaml("applicationDocs: []\n"), Token)).Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("items").EnumerateArray().Should().ContainSingle().Which.GetProperty("action").GetString().Should().Be("Remove");
    }

    [Fact]
    public async Task should_export_the_state_as_a_document_that_applies_back_without_changing_anything()
    {
        var name = Callers.Unique("export");
        await _callers.Admin.PostAsJsonAsync("/api/v1/sources", new { name, kind = "Knowledge", mode = "ReadOnly", path = Folder(name) }, Token);

        var exported = await _callers.Admin.GetAsync("/api/v1/config", Token);
        var text = await exported.Content.ReadAsStringAsync(Token);
        var back = await (await _callers.Admin.PutAsync("/api/v1/config?dryRun=true", Yaml(text), Token)).Content.ReadFromJsonAsync<JsonElement>(Token);

        exported.Content.Headers.ContentType!.MediaType.Should().Be("application/yaml");
        text.Should().Contain(name).And.Contain("knowledge:");
        back.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("name").GetString() == name).Should().ContainSingle().Which.GetProperty("action").GetString().Should().Be("Adopt");
    }

    [Fact]
    public async Task should_refuse_a_key_limited_to_some_sources_the_application_of_a_configuration_and_show_it_only_its_own_in_the_export()
    {
        var mine = Callers.Unique("scoped");
        var created = await _callers.Admin.PostAsJsonAsync("/api/v1/sources", new { name = mine, kind = "Knowledge", mode = "ReadOnly", path = Folder(mine) }, Token);
        var mineId = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        var service = await Callers.CreateAsync(_callers.Admin, new { name = Callers.Unique("svc"), kind = "Service", role = "Operator" });
        var key = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{service}/keys", new { name = "scoped", sources = new[] { mineId } }, Token);
        using var scoped = _factory.CreateKeyClient((await key.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!);

        var apply = await scoped.PutAsync("/api/v1/config", Yaml("knowledge: []\n"), Token);
        var export = await (await scoped.GetAsync("/api/v1/config", Token)).Content.ReadAsStringAsync(Token);

        apply.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        export.Should().Contain(mine);
        export.Split('\n').Count(line => line.Contains("name:", StringComparison.Ordinal)).Should().Be(1);
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Assistant")]
    [InlineData("Collector")]
    [InlineData("Monitor")]
    public async Task should_let_nobody_without_the_right_to_manage_sources_read_or_apply_a_configuration(string role)
    {
        var read = await _callers[role].GetAsync("/api/v1/config", Token);
        var apply = await _callers[role].PutAsync("/api/v1/config", Yaml("knowledge: []\n"), Token);

        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        apply.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task should_refuse_the_application_of_a_configuration_to_a_caller_who_is_not_signed_in()
    {
        var response = await _callers.Anonymous.PutAsync("/api/v1/config", Yaml("knowledge: []\n"), Token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_apply_the_file_it_is_given_at_start_and_be_ready_once_it_has()
    {
        await using var factory = new ConfigFileFactory();
        using var admin = await factory.SignedInClientAsync();

        var ready = await WaitAsync(async () => await (await admin.GetAsync("/health/ready", Token)).Content.ReadAsStringAsync(Token) == "Healthy");

        ready.Should().BeTrue("the configuration file is applied after the host starts");
        var sources = await admin.GetFromJsonAsync<JsonElement>("/api/v1/sources", Token);
        sources.EnumerateArray().Select(s => (s.GetProperty("name").GetString(), s.GetProperty("kind").GetString(), s.GetProperty("managed").GetBoolean()))
            .Should().BeEquivalentTo([("memory", "Memory", true), ("cours", "Knowledge", true)]);
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100, Token);
        }

        return false;
    }
}
