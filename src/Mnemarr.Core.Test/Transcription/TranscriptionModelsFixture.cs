using Microsoft.Extensions.Options;
using Mnemarr.Core.Transcription;

namespace Mnemarr.Core.Test.Transcription;

/// <summary>The catalog of models (ADR 0047): the product's own list, or a YAML file read strictly and again when it changes.</summary>
public sealed class TranscriptionModelsFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string File(string content)
    {
        var path = Path.Combine(_workspace.Root, "models.yaml");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static TranscriptionModels Models(string? file) => new(Options.Create(new TranscriptionOptions { ModelsFile = file }));

    [Fact]
    public void should_be_the_products_own_list_without_a_file_with_the_preferred_model_first()
    {
        var catalog = Models(null).Current();

        catalog.FromFile.Should().BeFalse();
        catalog.Error.Should().BeNull();
        catalog.Models[0].Id.Should().Be("gpt-transcribe");
        catalog.Models.Select(model => model.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void should_read_the_models_of_the_file_with_their_labels_and_prices()
    {
        var catalog = Models(File("models:\n  - { id: whisper-large-v3, label: Whisper large, pricePerMinute: 0 }\n  - id: gpt-transcribe\n")).Current();

        catalog.FromFile.Should().BeTrue();
        catalog.Models.Should().Equal(new TranscriptionModel("whisper-large-v3", "Whisper large", 0m), new TranscriptionModel("gpt-transcribe", "gpt-transcribe", null));
    }

    [Fact]
    public void should_read_the_file_again_when_it_changes()
    {
        var path = File("models:\n  - id: first\n");
        var models = Models(path);
        models.Current().Models.Single().Id.Should().Be("first");

        System.IO.File.WriteAllText(path, "models:\n  - id: second-model\n");
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        models.Current().Models.Single().Id.Should().Be("second-model");
    }

    [Theory]
    [InlineData("models:\n  - id: a\n    color: red\n", "not valid YAML")]
    [InlineData("models: []\n", "lists no model")]
    [InlineData("models:\n  - &x { id: a }\n", "anchors")]
    [InlineData("models:\n  - id: a\n  - id: a\n", "listed twice")]
    [InlineData("models:\n  - id: 'a b'\n", "letters, digits")]
    [InlineData("models:\n  - { id: a, pricePerMinute: -1 }\n", "price")]
    [InlineData("models: [\n", "not valid YAML")]
    public void should_fall_back_to_the_products_list_and_say_why_when_the_file_cannot_be_used(string content, string why)
    {
        var catalog = Models(File(content)).Current();

        catalog.FromFile.Should().BeFalse();
        catalog.Models.Should().Equal(TranscriptionModels.BuiltIn);
        catalog.Error.Should().Contain(why).And.Contain("the product's own list applies").And.NotContain(_workspace.Root, "a sentence for a page gives no path");
    }

    [Fact]
    public void should_say_that_a_file_that_is_not_there_cannot_be_read()
    {
        var catalog = Models(Path.Combine(_workspace.Root, "absent.yaml")).Current();

        catalog.Models.Should().Equal(TranscriptionModels.BuiltIn);
        catalog.Error.Should().Contain("cannot be read");
    }

    [Fact]
    public void should_refuse_a_file_larger_than_the_limit()
    {
        var catalog = Models(File("models:\n" + string.Concat(Enumerable.Repeat("  - id: a\n", 8000)))).Current();

        catalog.Error.Should().Contain("larger than");
    }
}
