using System.Net;

namespace Mnemarr.Integration.Test;

public sealed class ErrorResponseFixture : IClassFixture<FailingStatusFactory>
{
    private readonly HttpClient _client;

    public ErrorResponseFixture(FailingStatusFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task should_answer_a_problem_document_when_the_server_fails()
    {
        var response = await _client.GetAsync("/api/v1/system/status", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task should_not_reveal_the_failure_details_when_the_server_fails()
    {
        var response = await _client.GetAsync("/api/v1/system/status", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().NotContain("could not be read").And.NotContain("InvalidOperationException");
    }

    [Fact]
    public async Task should_keep_the_security_headers_when_the_server_fails()
    {
        var response = await _client.GetAsync("/api/v1/system/status", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle();
    }
}
