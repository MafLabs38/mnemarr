using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Mnemarr.Integration.Test;

/// <summary>
/// The deployment examples configure Mnemarr with <c>MNEMARR__DATAFOLDER</c>. Nothing else exercises that prefix: the
/// other fixtures set the folder directly, and the image's own default would hide a broken prefix.
/// </summary>
[Collection("Environment variables")]
[Trait("Category", "Integration")]
public sealed class EnvironmentConfigurationFixture
{
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task should_read_the_data_folder_from_the_environment_variable(bool folderExists, HttpStatusCode expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mnemarr-env-{Guid.NewGuid():N}");
        if (folderExists)
        {
            Directory.CreateDirectory(folder);
        }

        Environment.SetEnvironmentVariable("MNEMARR__DATAFOLDER", folder);
        try
        {
            await using var factory = new WebApplicationFactory<Program>();

            var response = await factory.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MNEMARR__DATAFOLDER", null);
            if (folderExists)
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
