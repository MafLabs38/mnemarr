using Mnemarr.Core.Notes;

namespace Mnemarr.Core.Test.Notes;

/// <summary>The anchor of a heading is written by the server and, with the same rule, by the interface: both must give the same text (see <c>headingSlug</c> in the frontend).</summary>
public sealed class HeadingsFixture
{
    [Theory]
    [InlineData("Part One", "part-one")]
    [InlineData("  Ça va ?  ", "ça-va-")]
    [InlineData("Under_score and-dash", "under_score-and-dash")]
    [InlineData("Code `x` & more", "code-x--more")]
    [InlineData("", "")]
    public void should_write_a_heading_as_an_anchor_the_way_the_interface_does(string heading, string slug) =>
        Headings.Slug(heading).Should().Be(slug);
}
