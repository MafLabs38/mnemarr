using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Sources;

public sealed class SourcesOptionsValidatorFixture
{
    private static bool Fails(params string[] roots) => new SourcesOptionsValidator().Validate(null, new SourcesOptions { Roots = roots }).Failed;

    [Fact]
    public void should_accept_no_root_and_absolute_roots()
    {
        Fails().Should().BeFalse();
        Fails(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "other")).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("relative/folder")]
    public void should_refuse_a_root_that_is_blank_or_not_absolute(string root)
    {
        Fails(Path.GetTempPath(), root).Should().BeTrue();
    }
}
