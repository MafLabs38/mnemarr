using System.Runtime.Versioning;
using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Test.Indexing;

public sealed class MarkdownFileScannerFixture : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnemarr-scan-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void should_find_markdown_files_in_nested_folders_with_paths_that_use_a_slash()
    {
        TestWorkspace.Write(_root, "01 - Java/streams.md", "x");
        TestWorkspace.Write(_root, "readme.MD", "x");
        TestWorkspace.Write(_root, "02 - DevOps/ci/pipeline.md", "x");
        TestWorkspace.Write(_root, "notes.txt", "x");

        var files = MarkdownFileScanner.Scan(_root).ToList();

        files.Select(file => file.RelativePath).Should().BeEquivalentTo("01 - Java/streams.md", "readme.MD", "02 - DevOps/ci/pipeline.md");
    }

    [Fact]
    public void should_give_the_first_folder_as_the_topic_of_a_note_and_nothing_at_the_root()
    {
        TestWorkspace.Write(_root, "04 - Angular/signals.md", "x");
        TestWorkspace.Write(_root, "04 - Angular/deep/er/note.md", "x");
        TestWorkspace.Write(_root, "root.md", "x");

        MarkdownFileScanner.Scan(_root).ToDictionary(file => file.RelativePath, file => file.Topic).Should().Equal(new Dictionary<string, string>
        {
            ["04 - Angular/signals.md"] = "04 - Angular",
            ["04 - Angular/deep/er/note.md"] = "04 - Angular",
            ["root.md"] = string.Empty,
        });
    }

    [Fact]
    public void should_not_enter_a_hidden_folder_such_as_git_or_obsidian()
    {
        TestWorkspace.Write(_root, ".obsidian/workspace.md", "x");
        TestWorkspace.Write(_root, ".git/notes.md", "x");
        TestWorkspace.Write(_root, "kept.md", "x");

        MarkdownFileScanner.Scan(_root).Select(file => file.RelativePath).Should().Equal("kept.md");
    }

    [Fact]
    public void should_not_take_a_file_that_is_too_large_for_a_note()
    {
        File.WriteAllBytes(Path.Combine(_root, "huge.md"), new byte[MarkdownFileScanner.MaxFileBytes + 1]);
        TestWorkspace.Write(_root, "small.md", "x");

        MarkdownFileScanner.Scan(_root).Select(file => file.RelativePath).Should().Equal("small.md");
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void should_not_follow_a_link_that_leaves_the_source()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creating a link needs no privilege on Linux only.");
        using var outside = new TempFolder();
        TestWorkspace.Write(outside.Path, "secret.md", "x");
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), outside.Path);
        File.CreateSymbolicLink(Path.Combine(_root, "file-link.md"), Path.Combine(outside.Path, "secret.md"));
        TestWorkspace.Write(_root, "kept.md", "x");

        MarkdownFileScanner.Scan(_root).Select(file => file.RelativePath).Should().Equal("kept.md");
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("mnemarr-outside-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
