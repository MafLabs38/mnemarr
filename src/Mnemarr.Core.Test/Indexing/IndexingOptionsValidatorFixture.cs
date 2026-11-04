using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Test.Indexing;

public sealed class IndexingOptionsValidatorFixture
{
    [Theory]
    [InlineData(1)]
    [InlineData(86_400)]
    public void should_accept_an_interval_between_one_second_and_one_day(int seconds)
    {
        new IndexingOptionsValidator().Validate(null, new IndexingOptions { ScanIntervalSeconds = seconds }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(86_401)]
    public void should_refuse_any_other_interval(int seconds)
    {
        new IndexingOptionsValidator().Validate(null, new IndexingOptions { ScanIntervalSeconds = seconds }).FailureMessage
            .Should().Contain("MNEMARR__INDEXING__SCANINTERVALSECONDS");
    }
}
