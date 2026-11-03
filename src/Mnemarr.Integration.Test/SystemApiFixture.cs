using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

[Collection("Host")]
[Trait("Category", "Integration")]
public sealed class SystemApiFixture
{
    private readonly HttpClient _client;

    public SystemApiFixture(MnemarrFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task should_return_the_version_runtime_and_uptime()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        status.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        status.GetProperty("runtimeVersion").GetString().Should().NotBeNullOrWhiteSpace();
        status.GetProperty("uptimeSeconds").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task should_return_exactly_the_documented_properties_and_nothing_about_the_host()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        status.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("version", "runtimeVersion", "startedAt", "uptimeSeconds");
    }

    [Fact]
    public async Task should_answer_not_found_for_an_unknown_api_path_instead_of_the_html_shell()
    {
        var response = await _client.GetAsync("/api/v1/nothing-here", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }

    [Fact]
    public async Task should_publish_the_openapi_document_with_the_status_path()
    {
        var document = await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        document.Should().Contain("/api/v1/system/status");
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
}
