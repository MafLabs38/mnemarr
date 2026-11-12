using Microsoft.Extensions.Configuration;
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
    [InlineData(64)]
    public void should_accept_a_note_limit_between_one_and_sixty_four_megabytes(int megabytes)
    {
        new IndexingOptionsValidator().Validate(null, new IndexingOptions { MaxNoteMegabytes = megabytes }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
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

    [Fact]
    public void should_read_the_note_limit_from_the_indexing_section_of_the_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Indexing:MaxNoteMegabytes"] = "7" }).Build();

        configuration.GetSection("Indexing").Get<IndexingOptions>()!.MaxNoteBytes.Should().Be(7 * 1024 * 1024);
    }
}
