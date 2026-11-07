using Mnemarr.Core.Collectors;

namespace Mnemarr.Integration.Test;

/// <summary>How the versions of the collector programs are ordered (ADR 0032).</summary>
public sealed class ProgramVersionsFixture
{
    private static int Compare(string a, string b)
    {
        ProgramVersions.TryParse(a, out var first).Should().BeTrue(a);
        ProgramVersions.TryParse(b, out var second).Should().BeTrue(b);
        return Math.Sign(ProgramVersions.Compare(first, second));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.2", 1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.0-dev.5", 1)]
    [InlineData("0.0.0-dev.12", "0.0.0-dev.9", 1)]
    [InlineData("0.0.0-dev.9", "0.0.0-dev.12", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    public void should_order_versions_by_their_precedence(string a, string b, int expected) => Compare(a, b).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("-1.2.3")]
    public void should_refuse_what_is_not_a_semantic_version(string text) => ProgramVersions.TryParse(text, out _).Should().BeFalse();
}
