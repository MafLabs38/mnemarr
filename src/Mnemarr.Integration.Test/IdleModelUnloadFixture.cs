using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Embeddings;
using Mnemarr.Embedder;

namespace Mnemarr.Integration.Test;

/// <summary>The embedding server lets go of its model when nobody asks (ADR 0045): it looks on a clock, keeps looking when the engine will not let go, and does nothing when the setting says never.</summary>
public sealed class IdleModelUnloadFixture
{
    // Says each time it is asked, so that a test waits for the asking and not for a delay.
    private sealed class Model : IIdleUnload
    {
        private readonly SemaphoreSlim _asked = new(0);
        private int _failuresLeft;

        public Model(int failures = 0) => _failuresLeft = failures;

        public List<TimeSpan> Asked { get; } = [];

        public bool UnloadIfIdle(TimeSpan idle)
        {
            lock (Asked)
            {
                Asked.Add(idle);
            }

            _asked.Release();
            if (Interlocked.Decrement(ref _failuresLeft) >= 0)
            {
                throw new InvalidOperationException("The engine would not let go.");
            }

            return true;
        }

        public Task<bool> WaitForAskingAsync(TimeSpan wait) => _asked.WaitAsync(wait, Token);

        // The loop of the service starts a moment after StartAsync: a tick of the clock before it waits is lost, so the clock goes on until the model is asked.
        public async Task<bool> AdvanceUntilAskedAsync(FakeTimeProvider clock, TimeSpan step)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                clock.Advance(step);
                if (await WaitForAskingAsync(TimeSpan.FromMilliseconds(100)))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IdleModelUnload ServiceOf(Model model, int minutes, FakeTimeProvider clock) =>
        new(model, Options.Create(new EmbedderOptions { IdleUnloadMinutes = minutes }), clock, NullLogger<IdleModelUnload>.Instance);

    [Fact]
    public async Task should_ask_the_model_to_let_go_every_minute_with_the_delay_of_the_setting()
    {
        var model = new Model();
        var clock = new FakeTimeProvider();
        using var service = ServiceOf(model, 10, clock);
        await service.StartAsync(Token);

        (await model.AdvanceUntilAskedAsync(clock, TimeSpan.FromMinutes(1))).Should().BeTrue("a minute is the longest it waits between two looks");
        (await model.AdvanceUntilAskedAsync(clock, TimeSpan.FromMinutes(1))).Should().BeTrue();
        await service.StopAsync(Token);

        model.Asked.Should().OnlyContain(idle => idle == TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task should_look_twice_as_often_as_a_delay_that_is_shorter_than_two_minutes()
    {
        var model = new Model();
        var clock = new FakeTimeProvider();
        using var service = ServiceOf(model, 1, clock);
        await service.StartAsync(Token);

        (await model.AdvanceUntilAskedAsync(clock, TimeSpan.FromSeconds(30))).Should().BeTrue("a delay of one minute is looked at every thirty seconds, not every minute");
        await service.StopAsync(Token);
        model.Asked.Should().OnlyContain(idle => idle == TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task should_keep_looking_when_the_engine_would_not_let_go_and_never_stop_the_server()
    {
        var model = new Model(failures: 1);
        var clock = new FakeTimeProvider();
        using var service = ServiceOf(model, 10, clock);
        await service.StartAsync(Token);

        (await model.AdvanceUntilAskedAsync(clock, TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await model.AdvanceUntilAskedAsync(clock, TimeSpan.FromMinutes(1))).Should().BeTrue("it looked again after the engine refused");

        service.ExecuteTask!.IsFaulted.Should().BeFalse("a fault of a background service stops the host");
        await service.StopAsync(Token);
    }

    [Fact]
    public async Task should_never_ask_when_the_delay_is_zero()
    {
        var model = new Model();
        var clock = new FakeTimeProvider();
        using var service = ServiceOf(model, 0, clock);

        await service.StartAsync(Token);
        clock.Advance(TimeSpan.FromHours(5));

        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), Token);
        service.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue("there is nothing to wait for: the service is done as soon as it starts");
        model.Asked.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(1440, true)]
    [InlineData(1441, false)]
    public void should_accept_a_delay_from_zero_to_a_day(int minutes, bool accepted)
    {
        var result = new EmbedderOptionsValidator().Validate(null, new EmbedderOptions { KeyFile = typeof(IdleModelUnloadFixture).Assembly.Location, IdleUnloadMinutes = minutes });

        result.Succeeded.Should().Be(accepted);
    }
}
