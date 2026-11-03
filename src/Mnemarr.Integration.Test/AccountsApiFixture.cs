using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The accounts, their keys, the roles and the audit log, as an administrator uses them.</summary>
public sealed class AccountsApiFixture : IClassFixture<MnemarrFactory>, IAsyncLifetime
{
    private readonly MnemarrFactory _factory;
    private HttpClient _admin = null!;

    public AccountsApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync() => _admin = await _factory.SignedInClientAsync();

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique() => $"acct-{Guid.NewGuid():N}"[..20];

    private async Task<JsonElement> CreateAsync(string kind, string role, string? password = null)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/accounts", new { name = Unique(), kind, role, password }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    [Fact]
    public async Task should_list_the_built_in_roles_with_their_permissions()
    {
        var roles = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/roles", Token);

        roles.EnumerateArray().Select(r => r.GetProperty("name").GetString()).Should().Equal("Admin", "Operator", "Reader", "Assistant", "Collector", "Monitor");
        var reader = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Reader");
        reader.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Equal("notes.read", "search");
    }

    [Fact]
    public async Task should_create_a_user_and_return_it_without_any_password_or_hash()
    {
        var account = await CreateAsync("User", "Reader", "a long enough password");

        account.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "name", "kind", "role", "enabled", "createdAt");
        var fetched = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{account.GetProperty("id").GetGuid()}", Token);
        fetched.GetProperty("role").GetString().Should().Be("Reader");
        (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/accounts", Token)).EnumerateArray().Select(a => a.GetProperty("name").GetString())
            .Should().Contain(account.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("Robot", "Reader", null)]
    [InlineData("User", "Reader", "short")]
    public async Task should_refuse_an_account_that_does_not_follow_the_rules_with_a_400_problem_document(string kind, string role, string? password)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/accounts", new { name = Unique(), kind, role, password }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task should_refuse_a_name_that_is_taken_with_a_409()
    {
        var first = await CreateAsync("Service", "Reader");

        var again = await _admin.PostAsJsonAsync("/api/v1/accounts", new { name = first.GetProperty("name").GetString()!.ToUpperInvariant(), kind = "Service", role = "Reader" }, Token);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task should_update_the_role_and_the_state_of_an_account_then_delete_it()
    {
        var id = (await CreateAsync("User", "Reader", "a long enough password")).GetProperty("id").GetGuid();

        var updated = await _admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Operator", enabled = false }, Token);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var account = await updated.Content.ReadFromJsonAsync<JsonElement>(Token);
        account.GetProperty("role").GetString().Should().Be("Operator");
        account.GetProperty("enabled").GetBoolean().Should().BeFalse();
        (await _admin.DeleteAsync($"/api/v1/accounts/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _admin.GetAsync($"/api/v1/accounts/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_refuse_an_update_that_leaves_out_the_enabled_field()
    {
        var id = (await CreateAsync("Service", "Reader")).GetProperty("id").GetGuid();

        var response = await _admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader" }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_say_404_for_an_account_that_does_not_exist()
    {
        var id = Guid.NewGuid();

        (await _admin.GetAsync($"/api/v1/accounts/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader", enabled = true }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync($"/api/v1/accounts/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.GetAsync($"/api/v1/accounts/{id}/keys", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.PutAsJsonAsync($"/api/v1/accounts/{id}/password", new { password = "a long enough password" }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_not_let_the_last_administrator_be_removed_disabled_or_demoted()
    {
        await using var factory = new MnemarrFactory();
        using var admin = await factory.SignedInClientAsync();
        var id = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounts", Token)).EnumerateArray().Single().GetProperty("id").GetGuid();

        (await admin.DeleteAsync($"/api/v1/accounts/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader", enabled = true }, Token)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Admin", enabled = false }, Token)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task should_set_a_password_for_a_user_and_refuse_one_for_a_service()
    {
        var user = (await CreateAsync("User", "Reader", "a long enough password"));
        var service = (await CreateAsync("Service", "Reader")).GetProperty("id").GetGuid();

        (await _admin.PutAsJsonAsync($"/api/v1/accounts/{user.GetProperty("id").GetGuid()}/password", new { password = "yet another password" }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _admin.PutAsJsonAsync($"/api/v1/accounts/{service}/password", new { password = "yet another password" }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var signIn = _factory.CreateBrowserClient();
        (await signIn.PostAsJsonAsync("/api/v1/auth/login", new { name = user.GetProperty("name").GetString(), password = "yet another password" }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task should_show_the_secret_of_a_key_once_and_never_again()
    {
        var id = (await CreateAsync("Service", "Assistant")).GetProperty("id").GetGuid();

        var created = await _admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "laptop", sources = new[] { Guid.NewGuid() } }, Token);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
        var secret = body.GetProperty("secret").GetString()!;
        secret.Should().StartWith("mnm_");
        body.GetProperty("key").GetProperty("prefix").GetString().Should().Be(secret[..12]);
        var listing = await (await _admin.GetAsync($"/api/v1/accounts/{id}/keys", Token)).Content.ReadAsStringAsync(Token);
        listing.Should().NotContain(secret).And.Contain("laptop");
        (await _admin.GetStringAsync("/api/v1/audit", Token)).Should().NotContain(secret);
    }

    [Fact]
    public async Task should_refuse_a_key_without_a_name_and_a_key_for_an_account_that_does_not_exist()
    {
        var id = (await CreateAsync("Service", "Reader")).GetProperty("id").GetGuid();

        (await _admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "" }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.PostAsJsonAsync($"/api/v1/accounts/{Guid.NewGuid()}/keys", new { name = "k" }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync($"/api/v1/accounts/{id}/keys/{Guid.NewGuid()}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_record_who_did_what_in_the_audit_log()
    {
        var account = await CreateAsync("Service", "Reader");
        var name = account.GetProperty("name").GetString();

        var events = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=50&action=account.created", Token);

        events.EnumerateArray().Should().Contain(e => e.GetProperty("target").GetString() == name && e.GetProperty("actor").GetString() == "admin");
        (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=1", Token)).GetArrayLength().Should().Be(1);
    }
}
