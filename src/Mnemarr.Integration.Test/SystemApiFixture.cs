using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

[Collection("Host")]
public sealed class SystemApiFixture : IAsyncLifetime
{
    private readonly MnemarrFactory _factory;
    private HttpClient _client = null!;

    public SystemApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync() => _client = await _factory.SignedInClientAsync();

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task should_return_exactly_the_documented_properties_in_the_documented_formats_and_nothing_about_the_host()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        status.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("version", "runtimeVersion", "startedAt", "uptimeSeconds");
        status.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        status.GetProperty("runtimeVersion").GetString().Should().NotBeNullOrWhiteSpace();
        // The interface reads this with `new Date(...)`: UTC, ISO 8601. The exact values are in UptimeFixture.
        status.GetProperty("startedAt").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?\+00:00$");
        status.GetProperty("uptimeSeconds").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task should_answer_a_problem_document_for_an_unknown_api_path_instead_of_the_html_shell()
    {
        var response = await _client.GetAsync("/api/v1/nothing-here", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task should_publish_an_openapi_document_that_says_what_it_is_and_lists_the_status_path()
    {
        var document = await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        document.Should().Contain("Mnemarr API").And.Contain("/api/v1/system/status");
    }

    [Theory]
    [InlineData("/api/v1/system/status")]
    [InlineData("/api/v1/nothing-here")]
    [InlineData("/health/live")]
    public async Task should_send_security_headers_on_every_kind_of_response(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Contain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task should_allow_inline_styles_for_diagrams_and_nothing_inline_or_dynamic_for_scripts()
    {
        var response = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        var policy = response.Headers.GetValues("Content-Security-Policy").Single();

        policy.Should().Contain("default-src 'self'").And.Contain("style-src 'self' 'unsafe-inline'");
        policy.Should().NotContain("script-src", "scripts follow default-src: the application's own files only").And.NotContain("unsafe-eval");
    }
}
