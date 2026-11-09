using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The state of the search by meaning over HTTP (ADR 0034): a Mnemarr with no model folder says it is off, and its search is by keywords as before.</summary>
public sealed class SemanticSearchApiFixture : IClassFixture<MnemarrFactory>
{
    private readonly MnemarrFactory _factory;

    public SemanticSearchApiFixture(MnemarrFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task should_say_that_the_search_by_meaning_is_off_when_nobody_gave_a_model()
    {
        var callers = await Callers.ForAsync(_factory);

        var status = await callers["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/search/status", Token);

        status.GetProperty("state").GetString().Should().Be("off");
        status.GetProperty("passages").GetInt32().Should().Be(0);
        status.TryGetProperty("reason", out var reason).Should().BeTrue();
        reason.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
