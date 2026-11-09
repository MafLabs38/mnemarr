using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Embeddings;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>
/// The real model against a reference made with the tools of its authors (<c>tokenizers</c> and ONNX Runtime in Python): the same token ids, and vectors that point
/// the same way. The model is 390 MB and is not in the repository: the test runs when <c>MNEMARR_TEST_MODEL</c> names its folder (<c>tools/native/fetch-model.sh</c>), and says it skipped otherwise. The reference
/// is <c>reference.json</c> (a list of text, ids, embedding), made by <c>reference.py</c>.
/// </summary>
public sealed class ModelReferenceFixture
{
    private static readonly string? Model = Environment.GetEnvironmentVariable("MNEMARR_TEST_MODEL");
    private static readonly string Reference = Path.Combine(AppContext.BaseDirectory, "Embeddings", "reference.json");

    private sealed record Expected(string Text, int[] Ids, float[] Embedding);

    private static List<Expected> Cases()
    {
        File.Exists(Reference).Should().BeTrue("reference.json is copied beside the tests (reference.py makes it)");
        return Read();
    }

    private static List<Expected> Read() =>
        JsonSerializer.Deserialize<List<Expected>>(File.ReadAllText(Reference), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void should_cut_every_text_into_the_ids_of_the_reference_tokenizer()
    {
        Assert.SkipUnless(Model is not null, "No model folder given.");
        var tokenizer = ByteLevelBpeTokenizer.Load(Path.Combine(Model!, "tokenizer.json"));

        foreach (var expected in Cases())
        {
            tokenizer.Encode(expected.Text, 32_768).Should().Equal(expected.Ids, $"the text {expected.Text}");
        }
    }

    [Fact]
    public void should_compute_vectors_that_point_the_way_of_the_reference()
    {
        Assert.SkipUnless(Model is not null, "No model folder given.");
        var options = new EmbeddingOptions { ModelFolder = Model, ExtensionPath = "unused", MaxTokens = 4096 };
        using var provider = new OnnxEmbeddingProvider(Options.Create(options), NullLogger<OnnxEmbeddingProvider>.Instance);
        provider.Status().Should().Be(new EmbeddingStatus(EmbeddingState.Ready));
        var cases = Cases();

        var vectors = provider.EmbedPassages(cases.Select(item => item.Text).ToList());

        for (var index = 0; index < cases.Count; index++)
        {
            // Summed in double: the same vector on another CPU or another build of the runtime differs in the last digits, not in the direction.
            var cosine = vectors[index].Zip(cases[index].Embedding, (left, right) => (double)left * right).Sum();
            cosine.Should().BeGreaterThan(0.999, $"the text {cases[index].Text}");
        }
    }

    [Fact]
    public void should_not_load_the_model_to_say_where_it_is_and_say_why_it_cannot_be_loaded_when_a_file_is_not_the_pinned_one()
    {
        var folder = Directory.CreateTempSubdirectory("mnemarr-model-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "model.onnx"), "not the model");
            File.WriteAllText(Path.Combine(folder, "tokenizer.json"), "{}");
            using var provider = new OnnxEmbeddingProvider(Options.Create(new EmbeddingOptions { ModelFolder = folder, ExtensionPath = "unused" }), NullLogger<OnnxEmbeddingProvider>.Instance);
            provider.Known().Should().BeNull("nothing has loaded it yet");

            var status = provider.Status();

            status.State.Should().Be(EmbeddingState.Failed);
            status.Reason.Should().Contain("not the one pinned").And.NotContain(folder, "the host's paths are not said to the people who ask");
            provider.Known().Should().Be(status);
            var embed = () => provider.EmbedQuery("hello");
            embed.Should().Throw<EmbeddingException>();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void should_refuse_a_model_folder_where_a_file_is_missing()
    {
        var folder = Directory.CreateTempSubdirectory("mnemarr-model-").FullName;
        try
        {
            using var provider = new OnnxEmbeddingProvider(Options.Create(new EmbeddingOptions { ModelFolder = folder, ExtensionPath = "unused" }), NullLogger<OnnxEmbeddingProvider>.Instance);

            provider.Status().Should().Match<EmbeddingStatus>(status => status.State == EmbeddingState.Failed && status.Reason!.Contains("is not in the model folder", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
