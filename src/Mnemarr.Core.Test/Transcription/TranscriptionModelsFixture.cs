using Microsoft.Extensions.Options;
using Mnemarr.Core.Transcription;

namespace Mnemarr.Core.Test.Transcription;

/// <summary>The catalog of models (ADR 0047): the provider's own list, or a YAML file read strictly and again when it changes.</summary>
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

    private static TranscriptionModels Models(string? file) =>
        new(Options.Create(new TranscriptionOptions { Provider = TranscriptionProviders.OpenAiCompatible, ModelsFile = file }), TranscriptionProviders.BuiltIn);

    [Fact]
    public void should_be_the_providers_own_list_without_a_file_with_the_preferred_model_first()
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
    [InlineData("models:\n  - { id: a }\n---\nmodels:\n  - { id: b }\n", "The file of the transcription models is one YAML document")]
    [InlineData("models:\n  - !secret { id: a }\n", "The file of the transcription models has no tags")]
    [InlineData("models:\n  - id: a\n  - id: a\n", "listed twice")]
    [InlineData("models:\n  - id: 'a b'\n", "letters, digits")]
    [InlineData("models:\n  - { id: a, pricePerMinute: -1 }\n", "price")]
    [InlineData("models: [\n", "not valid YAML")]
    [InlineData("models:\n  - \n", "entry is empty")]
    [InlineData("models:\n  - ~\n  - { id: a }\n", "entry is empty")]
    public void should_fall_back_to_the_providers_list_and_say_why_when_the_file_cannot_be_used(string content, string why)
    {
        var catalog = Models(File(content)).Current();

        catalog.FromFile.Should().BeFalse();
        catalog.Models.Should().Equal(OpenAiCompatibleTranscriptionProvider.Models);
        catalog.Error.Should().Contain(why).And.Contain("the provider's own list applies").And.NotContain(_workspace.Root, "a sentence for a page gives no path");
    }

    [Fact]
    public void should_say_that_a_file_that_is_not_there_cannot_be_read()
    {
        var catalog = Models(Path.Combine(_workspace.Root, "absent.yaml")).Current();

        catalog.Models.Should().Equal(OpenAiCompatibleTranscriptionProvider.Models);
        catalog.Error.Should().Contain("cannot be read");
    }

    [Fact]
    public void should_take_the_model_the_deployment_names_without_a_file_so_that_a_local_server_works_from_its_address_and_its_model()
    {
        var options = Options.Create(new TranscriptionOptions { Provider = TranscriptionProviders.OpenAiCompatible, Model = "Systran/faster-whisper-large-v3" });

        var catalog = new TranscriptionModels(options, TranscriptionProviders.BuiltIn).Current();

        catalog.Models[0].Should().Be(new TranscriptionModel("Systran/faster-whisper-large-v3", "Systran/faster-whisper-large-v3", null));
        catalog.Models.Skip(1).Should().Equal(OpenAiCompatibleTranscriptionProvider.Models);
    }

    [Theory]
    [InlineData("models: [\n")]
    [InlineData(null)]
    public void should_keep_the_model_the_deployment_names_when_the_file_cannot_be_used(string? content)
    {
        var path = content is null ? Path.Combine(_workspace.Root, "absent.yaml") : File(content);
        var options = Options.Create(new TranscriptionOptions { Provider = TranscriptionProviders.OpenAiCompatible, Model = "whisper-large-v3", ModelsFile = path });

        var catalog = new TranscriptionModels(options, TranscriptionProviders.BuiltIn).Current();

        catalog.Error.Should().NotBeNull();
        catalog.Models[0].Id.Should().Be("whisper-large-v3", "a typo in the file must not send another model to the deployment's server");
    }

    [Fact]
    public void should_offer_the_models_of_the_selected_provider_without_a_file()
    {
        var other = new OtherProvider();
        var options = Options.Create(new TranscriptionOptions { Provider = "Other" });

        var catalog = new TranscriptionModels(options, [.. TranscriptionProviders.BuiltIn, other]).Current();

        catalog.Models.Should().Equal(other.DefaultModels, "another service starts with its own models, not OpenAI's");
    }

    private sealed class OtherProvider : ITranscriptionProvider
    {
        public string Name => "Other";

        public IReadOnlyList<TranscriptionModel> DefaultModels { get; } = [new("local-large", "Local large", null)];

        public string? Validate(TranscriptionOptions options) => null;

        public ITranscriber Create(IOptions<TranscriptionOptions> options) => throw new NotSupportedException();

        public string? CheckSecrets(TranscriptionOptions options) => null;

        public string? Destination(TranscriptionOptions options) => "speech.homelab.example";

        public string Setup(TranscriptionOptions options) => "speech.homelab.example";
    }

    [Fact]
    public void should_refuse_a_file_larger_than_the_limit()
    {
        var label = new string('x', 2000);
        var catalog = Models(File("models:\n" + string.Concat(Enumerable.Range(0, 40).Select(i => $"  - {{ id: m{i}, label: {label} }}\n")))).Current();

        catalog.Error.Should().Contain("larger than 64 KB");
    }

    [Fact]
    public void should_refuse_more_models_than_the_limit()
    {
        var catalog = Models(File("models:\n" + string.Concat(Enumerable.Range(0, TranscriptionModels.MaxModels + 1).Select(i => $"  - id: m{i}\n")))).Current();

        catalog.Error.Should().Contain($"more than {TranscriptionModels.MaxModels}");
    }
}
