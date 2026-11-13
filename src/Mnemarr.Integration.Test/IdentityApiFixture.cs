using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The identity of the product at the bottom of the menu (ADR 0044), through the API: who reads it, who changes it, what is refused, how the logo is served.</summary>
public sealed class IdentityApiFixture : IClassFixture<MnemarrFactory>, IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private readonly MnemarrFactory _factory;
    private Callers _callers = null!;

    public IdentityApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The identity is one for the whole host: every test starts from the product's own.</summary>
    public async ValueTask InitializeAsync()
    {
        _callers = await Callers.ForAsync(_factory);
        await _callers.Admin.DeleteAsync("/api/v1/identity/logo", Token);
        await _callers.Admin.PutAsJsonAsync("/api/v1/identity", new { name = string.Empty, colors = new { name = string.Empty, version = string.Empty } }, Token);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static MultipartFormDataContent Form(byte[] content, string fileName)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    [Fact]
    public async Task should_be_the_product_for_everyone_who_is_signed_in_with_the_version_that_runs()
    {
        var identity = await _callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/identity", Token);

        identity.GetProperty("name").GetString().Should().Be("Mnemarr");
        identity.GetProperty("logoUrl").ValueKind.Should().Be(JsonValueKind.Null);
        identity.GetProperty("logoProblem").ValueKind.Should().Be(JsonValueKind.Null);
        identity.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        identity.GetProperty("colors").GetProperty("name").GetString().Should().Be("accent");
        identity.GetProperty("colors").GetProperty("version").GetString().Should().Be("accent");
    }

    [Fact]
    public async Task should_keep_the_rest_of_the_API_uncached_and_only_the_logo_cached_for_the_browser_of_the_person()
    {
        var json = await _callers["Reader"].GetAsync("/api/v1/identity", Token);

        json.Headers.CacheControl!.NoStore.Should().BeTrue("what the API says is for the person who asked");
        json.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Contain("default-src 'self'");
    }

    [Fact]
    public async Task should_let_an_administrator_name_the_lab_and_color_its_texts_and_show_it_to_everyone()
    {
        var response = await _callers.Admin.PutAsJsonAsync("/api/v1/identity", new { name = "MafLabs", colors = new { name = "#3ee0c5", version = "inkMuted" } }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seen = await _callers["Operator"].GetFromJsonAsync<JsonElement>("/api/v1/identity", Token);
        seen.GetProperty("name").GetString().Should().Be("MafLabs");
        seen.GetProperty("colors").GetProperty("name").GetString().Should().Be("#3ee0c5");
        seen.GetProperty("colors").GetProperty("version").GetString().Should().Be("inkMuted");
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Reader")]
    [InlineData("Assistant")]
    [InlineData("Collector")]
    [InlineData("Monitor")]
    public async Task should_refuse_to_whoever_is_not_an_administrator_every_change_of_the_identity(string caller)
    {
        var client = _callers[caller];
        var attempts = new[]
        {
            await client.PutAsJsonAsync("/api/v1/identity", new { name = "Taken over" }, Token),
            await client.PutAsync("/api/v1/identity/logo", Form(Png, "a.png"), Token),
            await client.DeleteAsync("/api/v1/identity/logo", Token),
        };

        attempts.Select(attempt => (int)attempt.StatusCode).Should().OnlyContain(status => status == 403);
        (await _callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/identity", Token)).GetProperty("name").GetString().Should().Be("Mnemarr");
    }

    [Theory]
    [InlineData("two\nlines", null)]
    [InlineData("MafLabs", "red")]
    [InlineData("MafLabs", "url(javascript:alert(1))")]
    public async Task should_refuse_a_name_or_a_color_that_is_not_valid_with_the_reason_and_change_nothing(string name, string? color)
    {
        var response = await _callers.Admin.PutAsJsonAsync("/api/v1/identity", new { name, colors = new { name = color } }, Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Token)).Should().Match("*name*");
        (await _callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/identity", Token)).GetProperty("name").GetString().Should().Be("Mnemarr");
    }

    [Fact]
    public async Task should_keep_an_uploaded_logo_and_serve_it_as_an_icon_cached_for_as_long_as_its_hash_is_in_the_address()
    {
        var uploaded = await _callers.Admin.PutAsync("/api/v1/identity/logo", Form(Png, "lab.png"), Token);

        uploaded.StatusCode.Should().Be(HttpStatusCode.OK);
        var url = (await uploaded.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("logoUrl").GetString()!;
        url.Should().StartWith("/api/v1/identity/logo?v=");
        var served = await _callers["Reader"].GetAsync(url, Token);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await served.Content.ReadAsByteArrayAsync(Token)).Should().Equal(Png);
        served.Headers.CacheControl!.ToString().Should().Contain("immutable").And.Contain("private");
        served.Headers.ETag.Should().NotBeNull();
        served.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        served.Headers.GetValues("Content-Security-Policy").Should().Equal("default-src 'none'; sandbox");
        (await _callers["Reader"].GetAsync("/api/v1/identity/logo?v=old", Token)).Headers.CacheControl!.ToString().Should().Contain("max-age=300").And.NotContain("immutable");
    }

    [Fact]
    public async Task should_answer_a_conditional_request_for_the_logo_without_sending_it_again()
    {
        var uploaded = await _callers.Admin.PutAsync("/api/v1/identity/logo", Form(Png, "lab.png"), Token);
        var url = (await uploaded.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("logoUrl").GetString()!;
        var first = await _callers["Reader"].GetAsync(url, Token);

        using var again = new HttpRequestMessage(HttpMethod.Get, url);
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        (await _callers["Reader"].SendAsync(again, Token)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Theory]
    [InlineData("lab.png", "not an image at all")]
    [InlineData("lab.gif", "GIF89a")]
    [InlineData("lab.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    public async Task should_refuse_a_file_that_is_not_an_image_of_an_accepted_kind_and_keep_the_logo_it_had(string fileName, string content)
    {
        await _callers.Admin.PutAsync("/api/v1/identity/logo", Form(Png, "lab.png"), Token);

        var refused = await _callers.Admin.PutAsync("/api/v1/identity/logo", Form(System.Text.Encoding.UTF8.GetBytes(content), fileName), Token);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/identity", Token)).GetProperty("logoUrl").GetString().Should().NotBeNull();
    }

    [Fact]
    public async Task should_refuse_a_request_without_a_file_and_a_file_above_a_mebibyte()
    {
        var none = await _callers.Admin.PutAsync("/api/v1/identity/logo", new MultipartFormDataContent { { new StringContent("x"), "other" } }, Token);
        var large = await _callers.Admin.PutAsync("/api/v1/identity/logo", Form([.. Png, .. new byte[1024 * 1024]], "big.png"), Token);

        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ((int)large.StatusCode).Should().BeOneOf(400, 413);
    }

    [Fact]
    public async Task should_use_the_product_s_own_logo_again_when_the_uploaded_one_is_removed()
    {
        await _callers.Admin.PutAsync("/api/v1/identity/logo", Form(Png, "lab.png"), Token);

        var removed = await _callers.Admin.DeleteAsync("/api/v1/identity/logo", Token);

        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await removed.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("logoUrl").ValueKind.Should().Be(JsonValueKind.Null);
        (await _callers.Admin.GetAsync("/api/v1/identity/logo", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_record_a_change_in_the_audit_log_under_the_name_of_the_administrator()
    {
        await _callers.Admin.PutAsJsonAsync("/api/v1/identity", new { name = "MafLabs" }, Token);
        await _callers.Admin.PutAsync("/api/v1/identity/logo", Form(Png, "lab.png"), Token);

        var events = await _callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=20", Token);

        events.EnumerateArray().Select(entry => entry.GetProperty("action").GetString()).Should().Contain(["identity.updated", "identity.logo.updated"]);
    }
}
