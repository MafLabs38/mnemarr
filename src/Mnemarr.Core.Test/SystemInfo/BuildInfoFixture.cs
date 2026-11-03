using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Core.Test.SystemInfo;

public sealed class BuildInfoFixture
{
    [Fact]
    public void should_show_the_version_it_was_built_with_and_no_commit_hash_appended_by_the_sdk()
    {
        BuildInfo.Version.Should().NotBeNullOrWhiteSpace().And.NotContain("+");
    }
}
