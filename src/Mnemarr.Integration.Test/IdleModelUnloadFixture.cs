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

        public Task<bool> WaitForAskingAsync() => _asked.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    // The clock of the test, which says when the service asks it for its timer and with what period: the service starts its loop a moment after StartAsync, and a tick of the clock that comes before the
    // timer exists is lost, so the test waits for the timer and not for a delay.
    private sealed class ClockOfTheTest : TimeProvider
    {
        private readonly TaskCompletionSource<TimeSpan> _timer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeTimeProvider Fake { get; } = new();

        public Task<TimeSpan> PeriodOfTheTimerAsync() => _timer.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();

        public override long GetTimestamp() => Fake.GetTimestamp();

        public override long TimestampFrequency => Fake.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Fake.CreateTimer(callback, state, dueTime, period);
            _timer.TrySetResult(period);
            return timer;
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IdleModelUnload ServiceOf(Model model, int minutes, TimeProvider clock) =>
        new(model, Options.Create(new EmbedderOptions { IdleUnloadMinutes = minutes }), clock, NullLogger<IdleModelUnload>.Instance);

    [Theory]
    [InlineData(2, 60)]
    [InlineData(1, 30)]
    public async Task should_look_every_minute_at_most_and_twice_as_often_as_a_delay_shorter_than_two_minutes(int minutes, int seconds)
    {
        var model = new Model();
        var clock = new ClockOfTheTest();
        using var service = ServiceOf(model, minutes, clock);
        await service.StartAsync(Token);

        (await clock.PeriodOfTheTimerAsync()).Should().Be(TimeSpan.FromSeconds(seconds));
        clock.Fake.Advance(TimeSpan.FromSeconds(seconds));

        (await model.WaitForAskingAsync()).Should().BeTrue("it asks the model when the period has passed");
        await service.StopAsync(Token);
        model.Asked.Should().OnlyContain(idle => idle == TimeSpan.FromMinutes(minutes), "it asks with the delay of the setting");
    }

    [Fact]
    public async Task should_keep_looking_when_the_engine_would_not_let_go()
    {
        var model = new Model(failures: 1);
        var clock = new ClockOfTheTest();
        using var service = ServiceOf(model, 10, clock);
        await service.StartAsync(Token);
        await clock.PeriodOfTheTimerAsync();

        clock.Fake.Advance(TimeSpan.FromMinutes(1));
        (await model.WaitForAskingAsync()).Should().BeTrue("the first look, where the engine refuses");
        clock.Fake.Advance(TimeSpan.FromMinutes(1));
        (await model.WaitForAskingAsync()).Should().BeTrue("it looked again after the engine refused: the server did not stop");

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
