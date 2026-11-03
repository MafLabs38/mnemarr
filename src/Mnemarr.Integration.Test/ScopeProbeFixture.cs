using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The scope of a key survives the real authentication: it is what a handler of a later vertical will read to filter by source.</summary>
public sealed class ScopeProbeFixture : IClassFixture<ScopeProbeFactory>
{
    private readonly ScopeProbeFactory _factory;

    public ScopeProbeFixture(ScopeProbeFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<string> KeyAsync(object? sources)
    {
        using var admin = await _factory.SignedInClientAsync();
        var id = await Callers.CreateAsync(admin, new { name = Callers.Unique("svc"), kind = "Service", role = "Assistant" });
        var response = await admin.PostAsJsonAsync($"/api/v1/accounts/{id}/keys", new { name = "k", sources }, Token);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString()!;
    }

    [Fact]
    public async Task should_read_the_sources_a_key_is_limited_to_from_the_claims_built_by_the_real_authentication()
    {
        var mine = Guid.NewGuid();
        using var client = _factory.CreateKeyClient(await KeyAsync(new[] { mine }));

        var scope = await client.GetFromJsonAsync<JsonElement>("/probe/scope", Token);

        scope.GetProperty("all").GetBoolean().Should().BeFalse();
        scope.GetProperty("sources").EnumerateArray().Select(s => s.GetGuid()).Should().Equal(mine);
    }

    [Fact]
    public async Task should_read_all_the_sources_for_a_key_that_is_not_limited_and_none_for_a_caller_who_is_not_signed_in()
    {
        using var unlimited = _factory.CreateKeyClient(await KeyAsync(null));
        using var anonymous = _factory.CreateClient();

        (await unlimited.GetFromJsonAsync<JsonElement>("/probe/scope", Token)).GetProperty("all").GetBoolean().Should().BeTrue();
        var none = await anonymous.GetFromJsonAsync<JsonElement>("/probe/scope", Token);
        none.GetProperty("all").GetBoolean().Should().BeFalse();
        none.GetProperty("sources").GetArrayLength().Should().Be(0);
    }
}
