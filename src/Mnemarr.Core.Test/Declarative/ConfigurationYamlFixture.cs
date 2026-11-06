using Mnemarr.Core.Declarative;
using Mnemarr.Core.Git;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Declarative;

public sealed class ConfigurationYamlFixture
{
    private const string Full = """
        memory:
          path: /vaults/memory
          mode: ReadWrite
        knowledge:
          - name: courses
            path: /vaults/courses
          - name: work
            path: /vaults/work
            mode: ReadOnly
        applicationDocs: []
        """;

    [Fact]
    public void should_read_a_document_in_the_shape_of_the_rules()
    {
        var document = ConfigurationYaml.Parse(Full);

        document.Memory.Should().BeEquivalentTo(new SourceEntry { Path = "/vaults/memory", Mode = "ReadWrite" });
        document.Knowledge!.Select(e => e.Name).Should().Equal("courses", "work");
        document.Knowledge![1].Mode.Should().Be("ReadOnly");
        document.ApplicationDocs.Should().BeEmpty("a section that is present and empty is a section that is managed");
    }

    [Fact]
    public void should_read_the_repository_of_a_source_and_write_it_back_in_an_export()
    {
        var document = ConfigurationYaml.Parse("""
            knowledge:
              - name: notes
                path: /vaults/notes
                repository:
                  url: https://github.example/org/notes
                  branch: main
                  credential: github
            """);

        document.Knowledge!.Single().Repository.Should().BeEquivalentTo(new RepositoryEntry { Url = "https://github.example/org/notes", Branch = "main", Credential = "github" });
        var exported = ConfigurationYaml.Export([new Source(Guid.NewGuid(), "notes", SourceKind.Knowledge, SourceMode.ReadOnly, "/vaults/notes", DateTimeOffset.UnixEpoch, Repository: new GitRepository("https://github.example/org/notes", null, null))]);
        exported.Should().Contain("repository:").And.Contain("url: https://github.example/org/notes").And.NotContain("branch").And.NotContain("credential");
        ConfigurationYaml.Parse(exported).Knowledge!.Single().Repository!.Url.Should().Be("https://github.example/org/notes");
    }

    [Fact]
    public void should_refuse_a_repository_key_that_is_not_known_such_as_a_token_in_the_document()
    {
        var parse = () => ConfigurationYaml.Parse("knowledge:\n  - name: n\n    path: /p\n    repository:\n      url: https://github.example/o/n\n      token: abc\n");

        parse.Should().Throw<RejectedException>();
    }

    [Fact]
    public void should_tell_a_section_that_is_absent_from_one_that_is_empty()
    {
        var document = ConfigurationYaml.Parse("knowledge: []\n");

        document.Knowledge.Should().NotBeNull();
        document.Memory.Should().BeNull();
        document.ApplicationDocs.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("# nothing but a comment\n")]
    public void should_read_an_empty_document_as_one_that_manages_nothing(string text)
    {
        var document = ConfigurationYaml.Parse(text);

        document.Should().BeEquivalentTo(new ConfigurationDocument());
    }

    [Theory]
    [InlineData("memry:\n  path: /x\n", "memry")]
    [InlineData("memory:\n  path: /x\n  colour: red\n", "colour")]
    [InlineData("knowledge: not-a-list\n", "line 1")]
    [InlineData("memory: [unterminated\n", "line")]
    [InlineData("knowledge:\n", "'knowledge' has no value")]
    [InlineData("memory: ~\napplicationDocs: []\n", "'memory' has no value")]
    public void should_refuse_what_it_does_not_know_with_the_line_instead_of_ignoring_it(string text, string mentioned)
    {
        var parse = () => ConfigurationYaml.Parse(text);

        parse.Should().Throw<RejectedException>().Which.Should().Match<RejectedException>(e => e.Problem == Problem.Invalid && e.Message.Contains(mentioned));
    }

    [Theory]
    [InlineData("memory: &m\n  path: /x\nknowledge:\n  - *m\n", "anchors or aliases")]
    [InlineData("memory: !!map\n  path: /x\n", "tags")]
    [InlineData("memory:\n  path: /x\n---\nmemory:\n  path: /y\n", "one YAML document")]
    public void should_refuse_what_makes_a_small_document_big_or_a_value_something_else(string text, string reason)
    {
        var parse = () => ConfigurationYaml.Parse(text);

        parse.Should().Throw<RejectedException>().Which.Message.Should().Contain(reason);
    }

    [Fact]
    public void should_refuse_a_document_nested_too_deep_before_building_anything_from_it()
    {
        var parse = () => ConfigurationYaml.Parse(new string('[', 200) + new string(']', 200));

        parse.Should().Throw<RejectedException>().Which.Message.Should().Contain("nested");
    }

    [Fact]
    public void should_refuse_a_document_that_is_too_large()
    {
        var parse = () => ConfigurationYaml.Parse("# " + new string('x', ConfigurationYaml.MaxBytes));

        parse.Should().Throw<RejectedException>().Which.Message.Should().Contain("larger than");
    }

    [Fact]
    public void should_write_the_sources_as_a_document_that_reads_back_the_same_and_has_every_section()
    {
        var at = DateTimeOffset.UnixEpoch;
        var sources = new[]
        {
            new Source(Guid.NewGuid(), "work", SourceKind.Knowledge, SourceMode.ReadOnly, "/vaults/work", at),
            new Source(Guid.NewGuid(), "memory", SourceKind.Memory, SourceMode.ReadWrite, "/vaults/memory", at, Managed: true),
            new Source(Guid.NewGuid(), "courses", SourceKind.Knowledge, SourceMode.ReadWrite, "/vaults/courses", at),
        };

        var text = ConfigurationYaml.Export(sources);
        var back = ConfigurationYaml.Parse(text);

        text.Should().Contain("memory:").And.Contain("knowledge:").And.Contain("applicationDocs: []");
        back.Memory.Should().BeEquivalentTo(new SourceEntry { Name = "memory", Path = "/vaults/memory", Mode = "ReadWrite" });
        back.Knowledge!.Select(e => e.Name).Should().Equal("courses", "work");
        back.ApplicationDocs.Should().BeEmpty();
    }

    [Fact]
    public void should_leave_the_memory_out_of_the_document_when_there_is_none()
    {
        ConfigurationYaml.Export([]).Should().NotContain("memory:");
    }
}
