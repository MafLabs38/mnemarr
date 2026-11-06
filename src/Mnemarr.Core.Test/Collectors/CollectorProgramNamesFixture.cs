using Microsoft.Extensions.Options;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Core.Test.Collectors;

public sealed class CollectorProgramNamesFixture : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mnemarr-programs-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// The names the CI gives the programs it puts in the image (the "Put the programs in the image" step of .github/workflows/ci.yml): if the server looks
    /// for other names, the image serves nothing and no test of the CI would say so.
    /// </summary>
    [Fact]
    public void should_serve_the_programs_under_the_names_the_ci_gives_them_in_the_image()
    {
        File.WriteAllText(Path.Combine(_folder, "mnemarr-collector-windows-x86_64.exe"), "windows");
        File.WriteAllText(Path.Combine(_folder, "mnemarr-collector-linux-x86_64"), "linux");
        File.WriteAllText(Path.Combine(_folder, ".gitkeep"), string.Empty);
        var downloads = new CollectorDownloads(Options.Create(new CollectorOptions { DownloadFolder = _folder }));

        downloads.Available().Select(download => download.Platform).Should().BeEquivalentTo("windows-x86_64", "linux-x86_64");
    }
}
