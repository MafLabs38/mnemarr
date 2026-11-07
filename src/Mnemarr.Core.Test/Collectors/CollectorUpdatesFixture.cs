using Microsoft.Extensions.Options;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Core.Test.Collectors;

/// <summary>What a Mnemarr carries of the updates of the collector (ADR 0032): the files that are served, the newest version of a feed, and who could update to it.</summary>
public sealed class CollectorUpdatesFixture : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"mnemarr-updates-{Guid.NewGuid():N}");

    public CollectorUpdatesFixture()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private CollectorUpdates Carrying() => new(Options.Create(new CollectorOptions { UpdateFolder = _folder }));

    private void Feed(string text) => File.WriteAllText(Path.Combine(_folder, "releases.win.json"), text);

    [Theory]
    [InlineData("releases.win.json", true)]
    [InlineData("MnemarrCollector-1.2.3-full.nupkg", true)]
    [InlineData("MnemarrCollector-0.0.1-dev.42-delta.nupkg", true)]
    [InlineData("MnemarrCollector-win-Setup.exe", true)]
    [InlineData("MnemarrCollector-win-Portable.zip", true)]
    [InlineData("appsettings.json", false)]
    [InlineData("secrets.json", false)]
    [InlineData("releases..json", false)]
    [InlineData("../releases.win.json", false)]
    [InlineData("a/b-Setup.exe", false)]
    [InlineData("a\\b-Setup.exe", false)]
    [InlineData("-full.nupkg", false)]
    [InlineData("x-Setup.exe.txt", false)]
    [InlineData(".hidden-Setup.exe", false)]
    [InlineData("", false)]
    public void should_serve_only_the_files_the_installer_makes(string name, bool served) => CollectorUpdates.IsServed(name).Should().Be(served);

    [Fact]
    public void should_carry_the_newest_full_version_and_ignore_what_is_not_one()
    {
        Feed("""{"Assets":[{"Version":"1.3.0","Type":"Full"},{"Version":"1.10.0","Type":"Full"},{"Version":"9.0.0","Type":"Delta"},{"Version":"not-a-version","Type":"Full"}]}""");

        Carrying().LatestVersion("windows-x86_64").Should().Be("1.10.0", "the newest full one, by number, not by text; a delta is not a version to install");
        Carrying().LatestVersion("linux-x86_64").Should().BeNull("there is no feed for that system");
        Carrying().LatestVersion(null).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json")]
    [InlineData("""{"Assets":"nothing"}""")]
    [InlineData("""{"Assets":[{"Version":"1.0.0","Type":"Delta"}]}""")]
    public void should_carry_nothing_when_the_feed_cannot_be_read_or_has_no_full_version(string text)
    {
        Feed(text);

        Carrying().LatestVersion("windows-x86_64").Should().BeNull();
        Carrying().NewerThan("0.0.1", canUpdate: true, "windows-x86_64").Should().BeNull();
    }

    [Fact]
    public void should_carry_nothing_when_there_is_no_folder()
    {
        var none = new CollectorUpdates(Options.Create(new CollectorOptions()));

        none.Folder.Should().BeNull();
        none.LatestVersion("windows-x86_64").Should().BeNull();
        none.InstallerName("windows-x86_64").Should().BeNull();
        none.Open("releases.win.json").Should().BeNull();
    }

    [Fact]
    public void should_find_the_installer_of_a_system_and_open_only_a_file_of_the_folder()
    {
        File.WriteAllText(Path.Combine(_folder, "MnemarrCollector-win-Setup.exe"), "setup");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not served");

        var updates = Carrying();

        updates.InstallerName("windows-x86_64").Should().Be("MnemarrCollector-win-Setup.exe");
        updates.InstallerName("linux-x86_64").Should().BeNull();
        updates.Open("MnemarrCollector-win-Setup.exe").Should().EndWith("MnemarrCollector-win-Setup.exe");
        updates.Open("notes.txt").Should().BeNull("it is not one of the installer's files");
        updates.Open("MnemarrCollector-0.0.1-full.nupkg").Should().BeNull("it is not there");
    }

    [Fact]
    public void should_not_follow_a_link_to_somewhere_else()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Links need rights on Windows.");
        var outside = Path.Combine(Path.GetTempPath(), $"mnemarr-outside-{Guid.NewGuid():N}.nupkg");
        File.WriteAllText(outside, "secret");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_folder, "MnemarrCollector-1.0.0-full.nupkg"), outside);

            Carrying().Open("MnemarrCollector-1.0.0-full.nupkg").Should().BeNull();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("1.0.0", true, "windows-x86_64", "1.4.0")]
    [InlineData("1.4.0", true, "windows-x86_64", null)]
    [InlineData("1.0.0", false, "windows-x86_64", null)]
    [InlineData("1.0.0", true, null, null)]
    [InlineData("1.0.0", true, "linux-x86_64", null)]
    public void should_say_the_version_only_to_a_machine_that_can_replace_itself_and_is_older(string version, bool can, string? platform, string? expected)
    {
        Feed("""{"Assets":[{"Version":"1.4.0","Type":"Full"}]}""");

        Carrying().NewerThan(version, can, platform).Should().Be(expected);
    }

    [Fact]
    public void should_read_the_feed_again_when_it_changes_and_not_when_it_does_not()
    {
        Feed("""{"Assets":[{"Version":"1.0.0","Type":"Full"}]}""");
        var updates = Carrying();

        updates.LatestVersion("windows-x86_64").Should().Be("1.0.0");
        Feed("""{"Assets":[{"Version":"1.1.0","Type":"Full"},{"Version":"1.0.0","Type":"Full"}]}""");
        File.SetLastWriteTimeUtc(Path.Combine(_folder, "releases.win.json"), DateTime.UtcNow.AddMinutes(1));

        updates.LatestVersion("windows-x86_64").Should().Be("1.1.0", "a feed that was replaced is read again, not kept");
    }

    [Fact]
    public void should_serve_a_name_of_the_longest_length_and_not_a_longer_one()
    {
        var longest = new string('a', 150 - "-Setup.exe".Length) + "-Setup.exe";

        CollectorUpdates.IsServed(longest).Should().BeTrue();
        CollectorUpdates.IsServed("a" + longest).Should().BeFalse();
    }
}
