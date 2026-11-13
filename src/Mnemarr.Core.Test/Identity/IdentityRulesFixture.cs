using System.Text;
using Mnemarr.Core.Identity;

namespace Mnemarr.Core.Test.Identity;

/// <summary>What the identity accepts (ADR 0044): a name on one line, a color that cannot carry CSS, an image that is what its extension says and cannot run code.</summary>
public sealed class IdentityRulesFixture
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void should_accept_a_name_of_at_most_forty_characters_on_one_line_and_refuse_the_others()
    {
        IdentityRules.ValidateName("Example Lab").Should().BeNull();
        IdentityRules.ValidateName(new string('a', IdentityRules.MaxNameLength)).Should().BeNull();
        IdentityRules.ValidateName(new string('a', IdentityRules.MaxNameLength + 1)).Should().Contain("at most 40");
        IdentityRules.ValidateName("two\nlines").Should().Contain("one line");
        IdentityRules.ValidateName("zero\u200Bwidth").Should().Contain("one line");
        IdentityRules.ValidateName("right\u202Eto-left").Should().Contain("one line");
        IdentityRules.ValidateName("Laboratoire café — 研究室").Should().BeNull("letters of any language are a name");
    }

    [Theory]
    [InlineData("accent")]
    [InlineData("inkMuted")]
    [InlineData("#fff")]
    [InlineData("#3ee0c5")]
    [InlineData("#3EE0C5")]
    public void should_accept_a_color_that_is_a_hex_or_a_token_of_the_theme(string color)
    {
        IdentityRules.IsColor(color).Should().BeTrue();
        IdentityRules.ValidateColor("name", color).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#ff")]
    [InlineData("#gggggg")]
    [InlineData("#3ee0c5ff")]
    [InlineData("var(--accent)")]
    [InlineData("red; background:url(x)")]
    [InlineData("url(javascript:alert(1))")]
    public void should_refuse_anything_else_so_that_a_value_never_carries_css(string color)
    {
        IdentityRules.IsColor(color).Should().BeFalse();
        IdentityRules.ValidateColor("version", color).Should().Contain("version color");
    }

    [Fact]
    public void should_accept_a_png_and_a_webp_by_their_signature_and_refuse_a_file_that_is_not_one()
    {
        var webp = Encoding.ASCII.GetBytes("RIFF").Concat(new byte[4]).Concat(Encoding.ASCII.GetBytes("WEBP")).Concat(new byte[4]).ToArray();

        IdentityRules.InspectLogo(Png, ".png").Format.Should().NotBeNull();
        IdentityRules.InspectLogo(webp, "webp").Format!.ContentType.Should().Be("image/webp");
        IdentityRules.InspectLogo(Text("not a png at all"), "png").Error.Should().Contain("not a valid png");
        IdentityRules.InspectLogo(Png, "webp").Error.Should().Contain("not a valid webp");
    }

    [Fact]
    public void should_refuse_a_file_that_is_only_a_signature_or_a_header_with_nothing_after_it()
    {
        IdentityRules.InspectLogo(Png[..8], "png").Format.Should().NotBeNull("a png is what its signature says; the browser tells whether it draws");
        IdentityRules.InspectLogo(Encoding.ASCII.GetBytes("RIFF").Concat(new byte[4]).Concat(Encoding.ASCII.GetBytes("WEBP")).ToArray(), "webp").Format.Should().BeNull();
    }

    [Fact]
    public void should_refuse_an_extension_that_is_not_an_image_an_empty_file_and_a_file_that_is_too_large()
    {
        IdentityRules.InspectLogo(Png, "gif").Error.Should().Contain("png, webp, svg");
        IdentityRules.InspectLogo(Png, string.Empty).Error.Should().NotBeNull();
        IdentityRules.InspectLogo([], "png").Error.Should().Contain("between 1 byte");
        IdentityRules.InspectLogo(new byte[IdentityRules.MaxLogoBytes + 1], "png").Error.Should().Contain("between 1 byte");
    }

    [Fact]
    public void should_accept_a_plain_svg()
    {
        var svg = Text("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 8 8"><style>.a{fill:url(#g)}</style><circle class="a" cx="4" cy="4" r="3"/></svg>""");

        IdentityRules.InspectLogo(svg, "svg").Format!.ContentType.Should().Be("image/svg+xml");
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div/></foreignObject></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><a href="javascript:alert(1)"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><a xlink:href="https://example.test/x"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image href="https://example.test/x.png"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>@import url(https://example.test/a.css);</style></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>.a{background:url(https://example.test/a.png)}</style></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" style="fill:url(https://example.test/a)"/>""")]
    [InlineData("""<!DOCTYPE svg [<!ENTITY x "y">]><svg xmlns="http://www.w3.org/2000/svg"/>""")]
    [InlineData("""<html xmlns="http://www.w3.org/1999/xhtml"/>""")]
    [InlineData("""<svg xmlns="http://example.test/other"/>""")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><unclosed>")]
    [InlineData("no markup at all")]
    public void should_refuse_an_svg_that_can_run_code_load_something_or_is_not_well_formed(string svg)
    {
        IdentityRules.InspectLogo(Text(svg), "svg").Format.Should().BeNull();
    }
}
