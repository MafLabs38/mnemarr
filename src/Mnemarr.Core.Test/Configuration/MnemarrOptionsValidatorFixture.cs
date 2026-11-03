using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Core.Test.Configuration;

public sealed class MnemarrOptionsValidatorFixture
{
    private static ValidateOptionsResult Validate(string folder) =>
        new MnemarrOptionsValidator().Validate(null, new MnemarrOptions { DataFolder = folder });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void should_refuse_a_data_folder_that_is_blank(string folder)
    {
        var result = Validate(folder);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("MNEMARR__DATAFOLDER");
    }

    [Fact]
    public void should_refuse_a_data_folder_that_is_not_an_absolute_path()
    {
        Validate("data/here").FailureMessage.Should().Contain("absolute");
    }

    [Fact]
    public void should_accept_an_absolute_path()
    {
        Validate(Path.GetTempPath()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void should_accept_the_default_the_image_uses()
    {
        Validate(new MnemarrOptions().DataFolder).Succeeded.Should().BeTrue();
    }
}
