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
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(512)]
    public void should_accept_a_note_limit_between_one_and_five_hundred_and_twelve_megabytes(int megabytes)
    {
        new IndexingOptionsValidator().Validate(null, new IndexingOptions { MaxNoteMegabytes = megabytes }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(513)]
    public void should_refuse_any_other_note_limit(int megabytes)
    {
        new IndexingOptionsValidator().Validate(null, new IndexingOptions { MaxNoteMegabytes = megabytes }).FailureMessage
            .Should().Contain("MNEMARR__INDEXING__MAXNOTEMEGABYTES");
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
