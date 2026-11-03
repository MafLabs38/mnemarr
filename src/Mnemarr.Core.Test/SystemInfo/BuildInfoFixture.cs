using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Core.Test.SystemInfo;

public sealed class BuildInfoFixture
{
    [Fact]
    public void should_show_the_version_it_was_built_with_and_no_commit_hash_appended_by_the_sdk()
    {
        // 0.0.0-dev in a test build, X.Y.Z.<run> or 0.0.0-<ref>.<run> in the image: never followed by "+<commit>".
        BuildInfo.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?$");
    }
}
