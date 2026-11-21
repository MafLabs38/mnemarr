using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Embeddings;
using Mnemarr.Embedder;

namespace Mnemarr.Integration.Test;

/// <summary>The embedding server lets go of its model when nobody asks (ADR 0045): it looks on a clock, never cuts a run, and does nothing when the setting says never.</summary>
public sealed class IdleModelUnloadFixture
{
    private sealed class Model : IIdleUnload
    {
        public List<TimeSpan> Asked { get; } = [];

        public bool UnloadIfIdle(TimeSpan idle)
        {
            lock (Asked)
            {
                Asked.Add(idle);
            }

            return true;
        }

        public int Count
        {
            get
            {
                lock (Asked)
                {
                    return Asked.Count;
                }
            }
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task should_ask_the_model_to_let_go_every_so_often_with_the_delay_of_the_setting()
    {
        var model = new Model();
        var clock = new FakeTimeProvider();
        using var service = new IdleModelUnload(model, Options.Create(new EmbedderOptions { IdleUnloadMinutes = 10 }), clock);
        await service.StartAsync(Token);

        // A minute is the longest it waits between two looks: the clock is the test's, so nothing is slept.
        for (var minute = 0; minute < 100 && model.Count < 2; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(10, Token);
        }

        await service.StopAsync(Token);

        model.Asked.Should().HaveCountGreaterThanOrEqualTo(2).And.OnlyContain(idle => idle == TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task should_never_ask_when_the_delay_is_zero()
    {
        var model = new Model();
        var clock = new FakeTimeProvider();
        using var service = new IdleModelUnload(model, Options.Create(new EmbedderOptions { IdleUnloadMinutes = 0 }), clock);
        await service.StartAsync(Token);

        clock.Advance(TimeSpan.FromHours(5));
        await Task.Delay(50, Token);
        await service.StopAsync(Token);

        model.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(1440, true)]
    [InlineData(1441, false)]
    public void should_accept_a_delay_from_zero_to_a_day(int minutes, bool accepted)
    {
        var keyFile = Path.GetTempFileName();
        try
        {
            var result = new EmbedderOptionsValidator().Validate(null, new EmbedderOptions { KeyFile = keyFile, IdleUnloadMinutes = minutes });

            result.Succeeded.Should().Be(accepted);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }
}
