using Mnemarr.Core.Consolidation;

namespace Mnemarr.Core.Test.Consolidation;

public sealed class ConsolidationOptionsFixture
{
    private static bool Valid(ConsolidationOptions options) => new ConsolidationOptionsValidator().Validate(null, options).Succeeded;

    [Fact]
    public void should_wait_five_minutes_of_silence_look_every_minute_and_run_sixty_times_a_day_at_most()
    {
        var options = new ConsolidationOptions();

        (options.IdleMinutes, options.PassSeconds, options.PerDay, options.PerPass).Should().Be((5, 60, 60, 3));
    }

    [Fact]
    public void should_be_off_by_default_and_ask_nothing_while_it_is_off()
    {
        new ConsolidationOptions().Enabled.Should().BeFalse();
        Valid(new ConsolidationOptions { PerPass = 0, Model = "bad model" }).Should().BeTrue();
    }

    [Fact]
    public void should_accept_the_defaults_once_on()
    {
        Valid(new ConsolidationOptions { Enabled = true }).Should().BeTrue();
    }

    [Theory]
    [InlineData("haiku", true)]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("", false)]
    [InlineData("a model", false)]
    [InlineData("x; rm", false)]
    public void should_take_a_model_name_and_nothing_else(string model, bool valid)
    {
        Valid(new ConsolidationOptions { Enabled = true, Model = model }).Should().Be(valid);
    }

    [Theory]
    [InlineData(0, 3, 30, false)]
    [InlineData(30, 0, 30, false)]
    [InlineData(30, 3, 0, false)]
    [InlineData(1440, 20, 1000, true)]
    [InlineData(1441, 3, 30, false)]
    public void should_keep_the_cost_of_a_pass_and_of_a_day_between_bounds(int idle, int perPass, int perDay, bool valid)
    {
        Valid(new ConsolidationOptions { Enabled = true, IdleMinutes = idle, PerPass = perPass, PerDay = perDay }).Should().Be(valid);
    }
}
