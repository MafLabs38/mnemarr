using System.Net;

namespace Mnemarr.Integration.Test;

/// <summary>What a browser may keep of the application: never the page that names the scripts (a new version would show hours late), always the scripts that carry a hash.</summary>
public sealed class FrontendCacheFixture : IClassFixture<FrontendFactory>
{
    private readonly FrontendFactory _factory;

    public FrontendCacheFixture(FrontendFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/machines")]
    public async Task should_make_the_browser_look_at_the_page_again_each_time(string path)
    {
        var response = await _factory.CreateBrowserClient().GetAsync(path, Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    [Fact]
    public async Task should_let_the_browser_keep_a_script_whose_name_carries_its_hash_for_good()
    {
        var response = await _factory.CreateBrowserClient().GetAsync("/assets/index-AbC123.js", Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromDays(365));
        response.Headers.CacheControl.ToString().Should().Contain("immutable");
    }
}
