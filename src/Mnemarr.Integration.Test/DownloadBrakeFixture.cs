using System.Net;

namespace Mnemarr.Integration.Test;

/// <summary>The brake of ADR 0029: four programs at a time. A client that does not read keeps the server writing, so the slots stay taken.</summary>
public sealed class DownloadBrakeFixture : IClassFixture<BigProgramFactory>
{
    private const string Program = "/api/v1/ingest/download/windows-x86_64";
    private readonly BigProgramFactory _factory;

    public DownloadBrakeFixture(BigProgramFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task should_refuse_a_fifth_download_at_once_with_a_wait_while_four_are_being_written_and_serve_again_when_they_end()
    {
        using var anonymous = _factory.CreateClient();
        var held = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                held.Add(await anonymous.GetAsync(Program, HttpCompletionOption.ResponseHeadersRead, Token));
            }

            held.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            using var fifth = await anonymous.GetAsync(Program, HttpCompletionOption.ResponseHeadersRead, Token);
            fifth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            fifth.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var response in held)
            {
                response.Dispose();
            }
        }

        HttpStatusCode served = default;
        for (var attempt = 0; attempt < 100 && served != HttpStatusCode.OK; attempt++)
        {
            using var again = await anonymous.GetAsync(Program, HttpCompletionOption.ResponseHeadersRead, Token);
            served = again.StatusCode;
            if (served != HttpStatusCode.OK)
            {
                await Task.Delay(50, Token);
            }
        }

        served.Should().Be(HttpStatusCode.OK, "the four slots were given back when the downloads ended");
    }
}
