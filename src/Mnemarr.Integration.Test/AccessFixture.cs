using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>What a person or a service may do, from the outside: who is let in, with what, and what is refused.</summary>
public sealed class AccessFixture : IClassFixture<MnemarrFactory>
{
    private readonly MnemarrFactory _factory;

    public AccessFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private async Task<string> CreateUserAsync(HttpClient admin, string role)
    {
        var name = Unique("user");
        var response = await admin.PostAsJsonAsync("/api/v1/accounts", new { name, kind = "User", role, password = "a long enough password" }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return name;
    }

    private async Task<(Guid Id, string Secret)> CreateServiceKeyAsync(HttpClient admin, string role)
    {
        var account = await (await admin.PostAsJsonAsync("/api/v1/accounts", new { name = Unique("svc"), kind = "Service", role }, Token))
            .Content.ReadFromJsonAsync<JsonElement>(Token);
        var id = account.GetProperty("id").GetGuid();
        var key = await (await admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "test" }, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        return (id, key.GetProperty("secret").GetString()!);
    }

    [Fact]
    public async Task should_sign_the_administrator_in_with_a_session_cookie_and_say_who_they_are()
    {
        using var client = _factory.CreateBrowserClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = MnemarrFactory.AdminName, password = MnemarrFactory.AdminPassword }, Token);

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = login.Headers.GetValues("Set-Cookie").Should().ContainSingle().Which.ToLowerInvariant();
        cookie.Should().Contain("mnemarr.session=").And.Contain("httponly").And.Contain("samesite=strict");
        var session = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/session", Token);
        session.GetProperty("name").GetString().Should().Be("admin");
        session.GetProperty("kind").GetString().Should().Be("User");
        session.GetProperty("role").GetString().Should().Be("Admin");
        session.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain(["accounts.manage", "system.read"]);
    }

    [Fact]
    public async Task should_answer_the_same_to_a_wrong_password_and_to_a_name_that_does_not_exist()
    {
        using var client = _factory.CreateBrowserClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = "admin", password = "not the password" }, Token);
        var unknownName = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = Unique("ghost"), password = "not the password" }, Token);

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownName.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var title = async (HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString();
        (await title(wrongPassword)).Should().Be(await title(unknownName));
        wrongPassword.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task should_lock_a_name_after_repeated_failures_with_a_429()
    {
        using var client = _factory.CreateBrowserClient();
        var name = Unique("locked");
        for (var i = 0; i < 5; i++)
        {
            (await client.PostAsJsonAsync("/api/v1/auth/login", new { name, password = "wrong password here" }, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var sixth = await client.PostAsJsonAsync("/api/v1/auth/login", new { name, password = "wrong password here" }, Token);

        sixth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task should_end_the_session_on_logout()
    {
        using var client = await _factory.SignedInClientAsync();
        (await client.GetAsync("/api/v1/system/status", Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsync("/api/v1/auth/logout", null, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/v1/system/status", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_refuse_a_write_that_carries_neither_the_request_header_nor_a_key()
    {
        using var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = "admin", password = MnemarrFactory.AdminPassword }, Token);

        login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        login.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task should_stop_a_session_at_once_when_its_account_is_disabled()
    {
        using var admin = await _factory.SignedInClientAsync();
        var name = await CreateUserAsync(admin, "Reader");
        using var user = await _factory.SignedInClientAsync(name, "a long enough password");
        (await user.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounts", Token)).EnumerateArray().First(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

        (await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader", enabled = false }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await user.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_apply_a_change_of_role_to_a_session_that_is_already_open()
    {
        using var admin = await _factory.SignedInClientAsync();
        var name = await CreateUserAsync(admin, "Reader");
        using var user = await _factory.SignedInClientAsync(name, "a long enough password");
        (await user.GetAsync("/api/v1/system/status", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var id = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounts", Token)).EnumerateArray().First(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

        (await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Operator", enabled = true }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await user.GetAsync("/api/v1/system/status", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Authorization", "Bearer ")]
    [InlineData("X-Api-Key", "")]
    public async Task should_let_a_service_in_with_its_key_in_either_header_and_without_the_request_header(string header, string prefix)
    {
        using var admin = await _factory.SignedInClientAsync();
        var (_, secret) = await CreateServiceKeyAsync(admin, "Operator");
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(header, prefix + secret);

        var read = await client.GetAsync("/api/v1/system/status", Token);
        var write = await client.PostAsync("/api/v1/auth/logout", null, Token);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        write.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task should_describe_a_service_as_a_service_with_the_permissions_of_its_role()
    {
        using var admin = await _factory.SignedInClientAsync();
        var (_, secret) = await CreateServiceKeyAsync(admin, "Collector");
        using var client = _factory.CreateKeyClient(secret);

        var session = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/session", Token);

        session.GetProperty("kind").GetString().Should().Be("Service");
        session.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Equal("ingest.write");
    }

    [Theory]
    [InlineData("mnm_this-key-does-not-exist")]
    [InlineData("mnm_")]
    public async Task should_refuse_a_key_that_is_not_valid_even_when_a_good_session_comes_with_it(string secret)
    {
        using var client = await _factory.SignedInClientAsync();
        client.DefaultRequestHeaders.Add("X-Api-Key", secret);

        var response = await client.GetAsync("/api/v1/system/status", Token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_refuse_a_key_once_it_is_revoked_and_when_its_account_is_disabled()
    {
        using var admin = await _factory.SignedInClientAsync();
        var (id, secret) = await CreateServiceKeyAsync(admin, "Reader");
        using var client = _factory.CreateKeyClient(secret);
        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var keyId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{id}/keys", Token)).EnumerateArray().Single().GetProperty("id").GetGuid();

        await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader", enabled = false }, Token);
        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader", enabled = true }, Token);
        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.DeleteAsync($"/api/v1/accounts/{id}/keys/{keyId}", Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_let_a_user_change_their_own_password_with_the_current_one_only()
    {
        using var admin = await _factory.SignedInClientAsync();
        var name = await CreateUserAsync(admin, "Reader");
        using var user = await _factory.SignedInClientAsync(name, "a long enough password");

        (await user.PutAsJsonAsync("/api/v1/auth/password", new { currentPassword = "not the password", newPassword = "another long password" }, Token))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await user.PutAsJsonAsync("/api/v1/auth/password", new { currentPassword = "a long enough password", newPassword = "another long password" }, Token))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var again = _factory.CreateBrowserClient();
        (await again.PostAsJsonAsync("/api/v1/auth/login", new { name, password = "a long enough password" }, Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await again.PostAsJsonAsync("/api/v1/auth/login", new { name, password = "another long password" }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The matrix of the specification: for every kind of caller, what each endpoint answers. A new endpoint or a new role changes
    /// this table on purpose.
    /// </summary>
    [Theory]
    [InlineData("anonymous", "GET", "/api/v1/system/status", 401)]
    [InlineData("anonymous", "GET", "/api/v1/accounts", 401)]
    [InlineData("anonymous", "GET", "/api/v1/auth/session", 401)]
    [InlineData("anonymous", "GET", "/openapi/v1.json", 401)]
    [InlineData("anonymous", "GET", "/api/v1/does-not-exist", 401)]
    [InlineData("Admin", "GET", "/api/v1/system/status", 200)]
    [InlineData("Admin", "GET", "/api/v1/accounts", 200)]
    [InlineData("Admin", "GET", "/api/v1/roles", 200)]
    [InlineData("Admin", "GET", "/api/v1/audit", 200)]
    [InlineData("Admin", "GET", "/openapi/v1.json", 200)]
    [InlineData("Admin", "GET", "/api/v1/does-not-exist", 404)]
    [InlineData("Operator", "GET", "/api/v1/system/status", 200)]
    [InlineData("Operator", "GET", "/api/v1/accounts", 403)]
    [InlineData("Operator", "GET", "/api/v1/audit", 403)]
    [InlineData("Operator", "GET", "/api/v1/roles", 403)]
    [InlineData("Reader", "GET", "/api/v1/system/status", 403)]
    [InlineData("Reader", "GET", "/api/v1/accounts", 403)]
    [InlineData("Reader", "GET", "/api/v1/auth/session", 200)]
    [InlineData("Reader", "GET", "/openapi/v1.json", 200)]
    [InlineData("Assistant", "GET", "/api/v1/system/status", 403)]
    [InlineData("Assistant", "GET", "/api/v1/accounts", 403)]
    [InlineData("Collector", "GET", "/api/v1/system/status", 403)]
    [InlineData("Collector", "GET", "/api/v1/accounts", 403)]
    [InlineData("Collector", "GET", "/api/v1/auth/session", 200)]
    [InlineData("Collector", "GET", "/api/v1/does-not-exist", 404)]
    public async Task should_answer_each_kind_of_caller_as_the_matrix_says(string caller, string method, string path, int expected)
    {
        using var admin = await _factory.SignedInClientAsync();
        using var client = caller switch
        {
            "anonymous" => _factory.CreateBrowserClient(),
            "Admin" => admin,
            _ when Array.IndexOf(["Operator", "Reader"], caller) >= 0 => await UserClientAsync(admin, caller),
            _ => _factory.CreateKeyClient((await CreateServiceKeyAsync(admin, caller)).Secret),
        };

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Token);

        ((int)response.StatusCode).Should().Be(expected);
    }

    private async Task<HttpClient> UserClientAsync(HttpClient admin, string role)
    {
        var name = await CreateUserAsync(admin, role);
        return await _factory.SignedInClientAsync(name, "a long enough password");
    }

    [Fact]
    public async Task should_serve_the_probes_to_anyone()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync("/health/live", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
