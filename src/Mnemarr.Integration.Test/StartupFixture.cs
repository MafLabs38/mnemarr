using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Integration.Test;

public sealed class StartupFixture
{
    [Theory]
    [InlineData("data/here")]
    [InlineData(" ")]
    public void should_refuse_to_start_on_a_data_folder_that_cannot_work(string folder)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<MnemarrOptions>(options => options.DataFolder = folder)));

        var start = () => factory.CreateClient();

        start.Should().Throw<OptionsValidationException>().WithMessage("*MNEMARR__DATAFOLDER*");
    }
}
