using System.Net;

namespace Mnemarr.Integration.Test;

[Collection("Host")]
[Trait("Category", "Integration")]
public sealed class HealthEndpointsFixture
{
    private readonly MnemarrFactory _factory;

    public HealthEndpointsFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task should_answer_live_when_the_process_runs()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task should_answer_ready_when_the_data_folder_is_writable()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

[Trait("Category", "Integration")]
public sealed class MissingDataFolderFixture : IClassFixture<MissingDataFolderFactory>
{
    private readonly MissingDataFolderFactory _factory;

    public MissingDataFolderFixture(MissingDataFolderFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task should_answer_not_ready_when_the_data_folder_is_missing()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task should_still_answer_live_when_the_data_folder_is_missing()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
