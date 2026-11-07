using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Mnemarr.Integration.Test;

/// <summary>What a person or a service may do, from the outside: who is let in, with what, and what is refused.</summary>
public sealed class AccessFixture : IClassFixture<MnemarrFactory>, IAsyncLifetime
{
    private readonly MnemarrFactory _factory;
    private Callers _callers = null!;

    public AccessFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync() => _callers = await Callers.ForAsync(_factory);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => Callers.Unique(prefix);

    private async Task<(string Name, Guid Id)> NewUserAsync(string role = "Reader")
    {
        var name = Unique("user");
        var id = await Callers.CreateAsync(_callers.Admin, new { name, kind = "User", role, password = Callers.Password });
        return (name, id);
    }

    /// <summary>A client that sends a cookie it was given and keeps none of its own: what a thief with a copied cookie has.</summary>
    private HttpClient WithCookie(string cookie)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("X-Mnemarr-Request", "1");
        return client;
    }

    private static string CookieOf(HttpResponseMessage login) => login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

    [Fact]
    public async Task should_sign_the_administrator_in_with_a_session_cookie_and_say_who_they_are()
    {
        using var client = _factory.CreateBrowserClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = MnemarrFactory.AdminName, password = MnemarrFactory.AdminPassword }, Token);

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        login.Headers.GetValues("Set-Cookie").Should().ContainSingle().Which.ToLowerInvariant().Should().Contain("mnemarr.session=").And.Contain("httponly").And.Contain("samesite=strict");
        var session = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/session", Token);
        session.GetProperty("accountId").GetGuid().Should().NotBeEmpty();
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

        var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new { name, password = "wrong password here" }, Token);

        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromMinutes(15), "the clock of the application is frozen, and the first failure was just now");
        (await locked.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("type").GetString().Should().Be("urn:mnemarr:problem:locked");
    }

    [Fact]
    public async Task should_end_the_session_on_the_server_when_the_person_signs_out_so_that_a_copied_cookie_is_dead()
    {
        var (name, _) = await NewUserAsync();
        using var browser = _factory.CreateBrowserClient();
        var login = await browser.PostAsJsonAsync("/api/v1/auth/login", new { name, password = Callers.Password }, Token);
        using var thief = WithCookie(CookieOf(login));
        (await thief.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await browser.PostAsync("/api/v1/auth/logout", null, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await browser.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await thief.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_end_the_other_sessions_when_a_person_changes_their_password_and_keep_the_one_that_changed_it()
    {
        var (name, _) = await NewUserAsync();
        using var laptop = _factory.CreateBrowserClient();
        using var phone = _factory.CreateBrowserClient();
        await laptop.PostAsJsonAsync("/api/v1/auth/login", new { name, password = Callers.Password }, Token);
        await phone.PostAsJsonAsync("/api/v1/auth/login", new { name, password = Callers.Password }, Token);

        (await laptop.PutAsJsonAsync("/api/v1/auth/password", new { currentPassword = Callers.Password, newPassword = "another long password" }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await laptop.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await phone.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_end_every_session_when_an_administrator_sets_the_password()
    {
        var (name, id) = await NewUserAsync();
        using var user = await _factory.SignedInClientAsync(name, Callers.Password);

        (await _callers.Admin.PutAsJsonAsync($"/api/v1/accounts/{id}/password", new { password = "another long password" }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await user.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_end_a_session_at_the_end_of_its_absolute_lifetime_however_much_it_is_used()
    {
        await using var factory = new ShortSessionFactory();
        using var client = await factory.SignedInClientAsync();
        // Used every 8 hours, the cookie slides along (it lasts 12 hours when idle, on the clock of the application): only the lifetime of the session on the server can end it.
        for (var i = 0; i < 2; i++)
        {
            factory.Clock.Advance(TimeSpan.FromHours(8));
            (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        factory.Clock.Advance(TimeSpan.FromHours(9));

        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_end_a_session_that_is_left_idle_for_longer_than_the_idle_limit_and_not_one_that_is_used()
    {
        await using var factory = new MnemarrFactory();
        using var used = await factory.SignedInClientAsync();
        using var idle = await factory.SignedInClientAsync();

        // The cookie lasts 12 hours when idle and slides with use: one that is used every 8 hours goes on, the other one does not.
        factory.Clock.Advance(TimeSpan.FromHours(8));
        (await used.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Clock.Advance(TimeSpan.FromHours(8));

        (await used.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await idle.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_refuse_a_key_once_it_has_expired()
    {
        await using var factory = new MnemarrFactory();
        using var admin = await factory.SignedInClientAsync();
        var id = await Callers.CreateAsync(admin, new { name = Unique("svc"), kind = "Service", role = "Reader" });
        var created = await admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "short", expiresAt = factory.Clock.GetUtcNow().AddDays(1) }, Token);
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!;
        using var service = factory.CreateKeyClient(secret);
        (await service.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromDays(2));

        (await service.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_stop_a_session_at_once_when_its_account_is_disabled_or_deleted()
    {
        var (nameOne, idOne) = await NewUserAsync();
        var (nameTwo, idTwo) = await NewUserAsync();
        using var one = await _factory.SignedInClientAsync(nameOne, Callers.Password);
        using var two = await _factory.SignedInClientAsync(nameTwo, Callers.Password);

        await _callers.Admin.PutAsJsonAsync($"/api/v1/accounts/{idOne}", new { role = "Reader", enabled = false }, Token);
        await _callers.Admin.DeleteAsync($"/api/v1/accounts/{idTwo}", Token);

        (await one.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await two.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_apply_a_change_of_role_to_a_session_that_is_already_open()
    {
        var (name, id) = await NewUserAsync();
        using var user = await _factory.SignedInClientAsync(name, Callers.Password);
        (await user.GetAsync("/api/v1/system/status", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await _callers.Admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Operator", enabled = true }, Token);

        (await user.GetAsync("/api/v1/system/status", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task should_refuse_a_write_that_carries_neither_the_request_header_nor_a_key()
    {
        using var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = "admin", password = MnemarrFactory.AdminPassword }, Token);

        login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        login.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Theory]
    [InlineData("/api/v1/accounts/{0}")]
    [InlineData("/API/v1/accounts/{0}")]
    [InlineData("/Api/V1/Accounts/{0}")]
    public async Task should_refuse_a_signed_in_browser_that_forgets_the_request_header_whatever_the_case_of_the_path(string template)
    {
        var (_, id) = await NewUserAsync();
        using var browser = _factory.CreateClient();
        using var signIn = _factory.CreateBrowserClient();
        var login = await signIn.PostAsJsonAsync("/api/v1/auth/login", new { name = "admin", password = MnemarrFactory.AdminPassword }, Token);
        browser.DefaultRequestHeaders.Add("Cookie", CookieOf(login));

        var response = await browser.DeleteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, id), Token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _callers.Admin.GetAsync($"/api/v1/accounts/{id}", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Authorization", "Bearer ")]
    [InlineData("Authorization", "bearer ")]
    [InlineData("X-Api-Key", "")]
    public async Task should_let_a_service_in_with_its_key_in_either_header_and_without_the_request_header(string header, string prefix)
    {
        var id = await Callers.CreateAsync(_callers.Admin, new { name = Unique("svc"), kind = "Service", role = "Operator" });
        var secret = await Callers.CreateKeyAsync(_callers.Admin, id);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(header, prefix + secret);

        (await client.GetAsync("/api/v1/system/status", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/v1/auth/logout", null, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("Authorization", "Bearer mnm_not-a-key-that-exists")]
    [InlineData("X-Api-Key", "mnm_not-a-key-that-exists")]
    public async Task should_answer_401_to_a_key_that_is_not_one_of_ours_and_never_fall_back_to_a_cookie(string header, string value)
    {
        using var client = await _factory.SignedInClientAsync();
        client.DefaultRequestHeaders.Add(header, value);

        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Bearer a.jwt.token")]
    [InlineData("Bearer ")]
    [InlineData("Basic YWRtaW46c2VjcmV0")]
    public async Task should_leave_a_credential_that_is_not_a_key_to_the_other_ways_in_and_so_to_the_cookie_if_there_is_one(string authorization)
    {
        using var signedIn = await _factory.SignedInClientAsync();
        signedIn.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        using var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);

        (await signedIn.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Assistant")]
    [InlineData("Collector")]
    [InlineData("Monitor")]
    public async Task should_tell_a_service_that_has_no_password_so_instead_of_calling_its_current_password_wrong(string role)
    {
        var response = await _callers[role].PutAsJsonAsync("/api/v1/auth/password", new { currentPassword = "none", newPassword = "another long password" }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task should_refuse_a_password_change_to_a_caller_who_is_not_signed_in()
    {
        var response = await _callers.Anonymous.PutAsJsonAsync("/api/v1/auth/password", new { currentPassword = "none", newPassword = "another long password" }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_let_no_cache_keep_what_the_api_says_the_secret_of_a_new_key_included()
    {
        var id = await Callers.CreateAsync(_callers.Admin, new { name = Unique("svc"), kind = "Service", role = "Reader" });

        var key = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "k" }, Token);
        var audit = await _callers.Admin.GetAsync("/api/v1/audit", Token);
        var shell = await _callers.Anonymous.GetAsync("/", Token);

        key.Headers.CacheControl!.NoStore.Should().BeTrue();
        audit.Headers.CacheControl!.NoStore.Should().BeTrue();
        shell.Headers.CacheControl?.NoStore.Should().NotBe(true, "the static shell may be cached: it holds nothing of anybody");
    }

    [Fact]
    public async Task should_describe_a_service_as_a_service_with_the_permissions_of_its_role()
    {
        var session = await _callers["Collector"].GetFromJsonAsync<JsonElement>("/api/v1/auth/session", Token);

        session.GetProperty("kind").GetString().Should().Be("Service");
        session.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Equal("ingest.write");
    }

    [Fact]
    public async Task should_refuse_a_key_once_it_is_revoked()
    {
        var id = await Callers.CreateAsync(_callers.Admin, new { name = Unique("svc"), kind = "Service", role = "Reader" });
        using var client = _factory.CreateKeyClient(await Callers.CreateKeyAsync(_callers.Admin, id));
        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var keyId = (await _callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{id}/keys", Token)).EnumerateArray().Single().GetProperty("id").GetGuid();

        (await _callers.Admin.DeleteAsync($"/api/v1/accounts/{id}/keys/{keyId}", Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_refuse_a_key_while_its_account_is_disabled()
    {
        var id = await Callers.CreateAsync(_callers.Admin, new { name = Unique("svc"), kind = "Service", role = "Reader" });
        using var client = _factory.CreateKeyClient(await Callers.CreateKeyAsync(_callers.Admin, id));

        await _callers.Admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader", enabled = false }, Token);
        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _callers.Admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = "Reader", enabled = true }, Token);

        (await client.GetAsync("/api/v1/auth/session", Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task should_not_give_a_key_to_a_person_so_that_nobody_gets_a_bearer_credential_that_can_create_administrators()
    {
        var (_, id) = await NewUserAsync("Admin");

        var response = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "script" }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_not_give_a_key_limited_to_no_source_because_it_would_read_as_all_of_them()
    {
        var id = await Callers.CreateAsync(_callers.Admin, new { name = Unique("svc"), kind = "Service", role = "Reader" });

        var response = await _callers.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "k", sources = Array.Empty<Guid>() }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_count_the_wrong_current_passwords_so_that_a_stolen_session_cannot_guess_the_password()
    {
        var (name, _) = await NewUserAsync();
        using var user = await _factory.SignedInClientAsync(name, Callers.Password);

        for (var i = 0; i < 5; i++)
        {
            (await user.PutAsJsonAsync("/api/v1/auth/password", new { currentPassword = "not the password", newPassword = "another long password" }, Token))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await user.PutAsJsonAsync("/api/v1/auth/password", new { currentPassword = Callers.Password, newPassword = "another long password" }, Token))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task should_let_a_user_change_their_own_password_with_the_current_one_and_sign_in_with_the_new_one()
    {
        var (name, _) = await NewUserAsync();
        using var user = await _factory.SignedInClientAsync(name, Callers.Password);

        (await user.PutAsJsonAsync("/api/v1/auth/password", new { currentPassword = Callers.Password, newPassword = "another long password" }, Token))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var again = _factory.CreateBrowserClient();
        (await again.PostAsJsonAsync("/api/v1/auth/login", new { name, password = "another long password" }, Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"a\"}")]
    [InlineData("{\"name\":\"a\",\"password\":null}")]
    [InlineData("not json")]
    public async Task should_answer_a_malformed_sign_in_with_a_400_and_never_a_server_error(string body)
    {
        using var client = _factory.CreateBrowserClient();

        var response = await client.PostAsync("/api/v1/auth/login", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Reader")]
    [InlineData("Assistant")]
    [InlineData("Collector")]
    [InlineData("Monitor")]
    public async Task should_refuse_to_whoever_is_not_an_administrator_the_reading_of_one_account_and_of_its_keys(string caller)
    {
        var victim = _callers.IdOf("Assistant");
        var client = _callers[caller];

        (await client.GetAsync($"/api/v1/accounts/{victim}", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/v1/accounts/{victim}/keys", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task should_read_the_audit_log_page_by_page_over_http()
    {
        var marker = Unique("paged");
        for (var i = 0; i < 3; i++)
        {
            await _callers.Admin.PostAsJsonAsync("/api/v1/accounts", new { name = $"{marker}-{i}", kind = "Service", role = "Reader" }, Token);
        }

        var first = await _callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=2&action=account.created", Token);
        var second = await _callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/audit?limit=2&action=account.created&before={first[1].GetProperty("id").GetInt64()}", Token);

        first.EnumerateArray().Select(e => e.GetProperty("target").GetString()).Should().Equal($"{marker}-2", $"{marker}-1");
        second.EnumerateArray().First().GetProperty("target").GetString().Should().Be($"{marker}-0");
    }


    /// <summary>
    /// The matrix of the specification: for every kind of caller, what each endpoint answers, writes included. A new endpoint or a new role
    /// changes this table on purpose.
    /// </summary>
    [Theory]
    [InlineData("anonymous", "GET", "/api/v1/system/status", 401)]
    [InlineData("anonymous", "GET", "/api/v1/accounts", 401)]
    [InlineData("anonymous", "GET", "/api/v1/auth/session", 401)]
    [InlineData("anonymous", "GET", "/openapi/v1.json", 401)]
    [InlineData("anonymous", "GET", "/api/v1/does-not-exist", 401)]
    [InlineData("anonymous", "POST", "/api/v1/accounts", 401)]
    [InlineData("anonymous", "DELETE", "/api/v1/accounts/00000000-0000-0000-0000-000000000001", 401)]
    [InlineData("Admin", "GET", "/api/v1/system/status", 200)]
    [InlineData("Admin", "GET", "/api/v1/accounts", 200)]
    [InlineData("Admin", "GET", "/api/v1/roles", 200)]
    [InlineData("Admin", "GET", "/api/v1/audit", 200)]
    [InlineData("Admin", "GET", "/openapi/v1.json", 200)]
    [InlineData("Admin", "GET", "/api/v1/does-not-exist", 404)]
    [InlineData("Operator", "GET", "/api/v1/system/status", 200)]
    [InlineData("Operator", "GET", "/api/v1/accounts", 403)]
    [InlineData("Admin", "GET", "/api/v1/sources", 200)]
    [InlineData("Operator", "GET", "/api/v1/sources", 200)]
    [InlineData("Operator", "GET", "/api/v1/sources/kinds", 200)]
    [InlineData("Reader", "GET", "/api/v1/sources/kinds", 403)]
    [InlineData("Reader", "GET", "/api/v1/sources", 403)]
    [InlineData("Assistant", "GET", "/api/v1/sources", 403)]
    [InlineData("Collector", "GET", "/api/v1/sources", 403)]
    [InlineData("Monitor", "GET", "/api/v1/sources", 403)]
    [InlineData("anonymous", "GET", "/api/v1/sources", 401)]
    [InlineData("Reader", "POST", "/api/v1/sources/00000000-0000-0000-0000-000000000001/sync", 403)]
    [InlineData("Assistant", "POST", "/api/v1/sources/00000000-0000-0000-0000-000000000001/sync", 403)]
    [InlineData("Operator", "POST", "/api/v1/sources/00000000-0000-0000-0000-000000000001/sync", 404)]
    [InlineData("anonymous", "POST", "/api/v1/sources/00000000-0000-0000-0000-000000000001/sync", 401)]
    [InlineData("Reader", "GET", "/api/v1/search?q=anything", 200)]
    [InlineData("Assistant", "GET", "/api/v1/search?q=anything", 200)]
    [InlineData("Operator", "GET", "/api/v1/search?q=anything", 200)]
    [InlineData("Collector", "GET", "/api/v1/search?q=anything", 403)]
    [InlineData("Monitor", "GET", "/api/v1/search?q=anything", 403)]
    [InlineData("anonymous", "GET", "/api/v1/search?q=anything", 401)]
    [InlineData("Reader", "GET", "/api/v1/search/status", 200)]
    [InlineData("Assistant", "GET", "/api/v1/search/status", 200)]
    [InlineData("Monitor", "GET", "/api/v1/search/status", 403)]
    [InlineData("Collector", "GET", "/api/v1/search/status", 403)]
    [InlineData("anonymous", "GET", "/api/v1/search/status", 401)]
    [InlineData("Reader", "GET", "/api/v1/notes", 200)]
    [InlineData("Assistant", "GET", "/api/v1/notes/tags", 200)]
    [InlineData("Reader", "GET", "/api/v1/notes/00000000-0000-0000-0000-000000000001/a.md", 404)]
    [InlineData("Reader", "GET", "/api/v1/sources/00000000-0000-0000-0000-000000000001/files/a.png", 404)]
    [InlineData("Collector", "GET", "/api/v1/notes", 403)]
    [InlineData("Monitor", "GET", "/api/v1/notes/tags", 403)]
    [InlineData("anonymous", "GET", "/api/v1/notes/tags", 401)]
    [InlineData("Monitor", "GET", "/api/v1/notes/00000000-0000-0000-0000-000000000001/a.md", 403)]
    [InlineData("Monitor", "GET", "/api/v1/sources/00000000-0000-0000-0000-000000000001/files/a.png", 403)]
    [InlineData("anonymous", "GET", "/api/v1/notes", 401)]
    [InlineData("anonymous", "GET", "/api/v1/sources/00000000-0000-0000-0000-000000000001/files/a.png", 401)]
    [InlineData("Operator", "GET", "/api/v1/audit", 403)]
    [InlineData("Monitor", "GET", "/api/v1/audit", 403)]
    [InlineData("Monitor", "GET", "/api/v1/roles", 403)]
    [InlineData("Assistant", "GET", "/api/v1/auth/session", 200)]
    [InlineData("Reader", "GET", "/api/v1/accounts/00000000-0000-0000-0000-000000000001", 403)]
    [InlineData("Operator", "GET", "/api/v1/roles", 403)]
    [InlineData("Operator", "GET", "/api/v1/auth/session", 200)]
    [InlineData("Operator", "GET", "/openapi/v1.json", 200)]
    [InlineData("Reader", "GET", "/api/v1/accounts", 403)]
    [InlineData("Reader", "GET", "/api/v1/audit", 403)]
    [InlineData("Reader", "GET", "/api/v1/roles", 403)]
    [InlineData("Assistant", "GET", "/api/v1/audit", 403)]
    [InlineData("Assistant", "GET", "/api/v1/roles", 403)]
    [InlineData("Collector", "GET", "/api/v1/audit", 403)]
    [InlineData("Collector", "GET", "/api/v1/roles", 403)]
    [InlineData("Collector", "GET", "/api/v1/accounts", 403)]
    [InlineData("Monitor", "GET", "/api/v1/system/status", 200)]
    [InlineData("Monitor", "GET", "/api/v1/accounts", 403)]
    [InlineData("Monitor", "GET", "/api/v1/auth/session", 200)]
    [InlineData("Reader", "GET", "/api/v1/system/status", 403)]
    [InlineData("Reader", "GET", "/api/v1/auth/session", 200)]
    [InlineData("Reader", "GET", "/openapi/v1.json", 200)]
    [InlineData("Assistant", "GET", "/api/v1/system/status", 403)]
    [InlineData("Assistant", "GET", "/api/v1/accounts", 403)]
    [InlineData("Collector", "GET", "/api/v1/system/status", 403)]
    [InlineData("Collector", "GET", "/api/v1/auth/session", 200)]
    [InlineData("Collector", "GET", "/api/v1/does-not-exist", 404)]
    public async Task should_answer_each_kind_of_caller_as_the_matrix_says_for_a_read(string caller, string method, string path, int expected)
    {
        var client = caller == "anonymous" ? _callers.Anonymous : _callers[caller];

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Token);

        ((int)response.StatusCode).Should().Be(expected);
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Reader")]
    [InlineData("Assistant")]
    [InlineData("Collector")]
    [InlineData("Monitor")]
    public async Task should_refuse_every_write_of_accounts_to_whoever_is_not_an_administrator_and_change_nothing(string caller)
    {
        var victim = await Callers.CreateAsync(_callers.Admin, new { name = Unique("victim"), kind = "Service", role = "Reader" });
        var client = _callers[caller];
        var attempts = new[]
        {
            await client.PostAsJsonAsync("/api/v1/accounts", new { name = Unique("new"), kind = "Service", role = "Reader" }, Token),
            await client.PutAsJsonAsync($"/api/v1/accounts/{victim}", new { role = "Collector", enabled = true }, Token),
            await client.PutAsJsonAsync($"/api/v1/accounts/{_callers.IdOf(caller)}", new { role = "Operator", enabled = true }, Token),
            await client.PutAsJsonAsync($"/api/v1/accounts/{victim}/password", new { password = "another long password" }, Token),
            await client.PostAsJsonAsync($"/api/v1/accounts/{victim}/keys", new { name = "k" }, Token),
            await client.DeleteAsync($"/api/v1/accounts/{victim}", Token),
        };

        attempts.Select(r => (int)r.StatusCode).Should().OnlyContain(status => status == 403);
        (await _callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{victim}", Token)).GetProperty("role").GetString().Should().Be("Reader");
        (await _callers.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{_callers.IdOf(caller)}", Token)).GetProperty("role").GetString().Should().Be(caller);
    }
}
