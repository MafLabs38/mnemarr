using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
    public void should_run_on_the_cpu_when_the_gpu_is_wanted_but_not_there_and_the_device_is_auto()
    {
        Assert.SkipUnless(Model is not null, "No model folder given.");
        var options = new EmbeddingOptions { ModelFolder = Model, ExtensionPath = "unused", Device = EmbeddingDevice.Auto };
        using var provider = new OnnxEmbeddingProvider(Options.Create(options), NullLogger<OnnxEmbeddingProvider>.Instance);

        provider.Status().Should().Be(new EmbeddingStatus(EmbeddingState.Ready));

        provider.Device.Should().Be("cpu", "the engine of this build has no GPU provider, and Auto means the search goes on");
        provider.EmbedPassages(["a passage"]).Should().ContainSingle();
    }

    [Fact]
    public void should_not_load_the_model_when_the_gpu_is_required_and_cannot_be_used()
    {
        Assert.SkipUnless(Model is not null, "No model folder given.");
        var options = new EmbeddingOptions { ModelFolder = Model, ExtensionPath = "unused", Device = EmbeddingDevice.Cuda };
        using var provider = new OnnxEmbeddingProvider(Options.Create(options), NullLogger<OnnxEmbeddingProvider>.Instance);

        provider.Status().State.Should().Be(EmbeddingState.Failed);
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
    public void should_let_go_of_the_model_when_nobody_asked_and_compute_the_same_vector_after_it_is_built_again()
    {
        Assert.SkipUnless(Model is not null, "No model folder given.");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero));
        var options = new EmbeddingOptions { ModelFolder = Model, ExtensionPath = "unused" };
        using var provider = new OnnxEmbeddingProvider(Options.Create(options), NullLogger<OnnxEmbeddingProvider>.Instance, clock);
        provider.Status().Should().Be(new EmbeddingStatus(EmbeddingState.Ready));
        var before = provider.EmbedPassages(["a passage"])[0];
        var tenMinutes = TimeSpan.FromMinutes(10);

        clock.Advance(TimeSpan.FromMinutes(9));
        provider.UnloadIfIdle(tenMinutes).Should().BeFalse("it was used nine minutes ago");
        clock.Advance(TimeSpan.FromMinutes(2));
        provider.UnloadIfIdle(tenMinutes).Should().BeTrue("nobody asked for eleven minutes");
        provider.UnloadIfIdle(tenMinutes).Should().BeFalse("there is nothing left to let go of");
        provider.Known().Should().Be(new EmbeddingStatus(EmbeddingState.Ready), "the files were verified and are not asked again: the model only sleeps");

        provider.EmbedPassages(["a passage"])[0].Should().Equal(before, "the same model computes the same vector once it is built again");
        clock.Advance(TimeSpan.FromMinutes(5));
        provider.UnloadIfIdle(tenMinutes).Should().BeFalse("the request that built it again counts as a use");
    }

    [Fact]
    public async Task should_never_fail_a_request_that_meets_an_unload()
    {
        Assert.SkipUnless(Model is not null, "No model folder given.");
        var options = new EmbeddingOptions { ModelFolder = Model, ExtensionPath = "unused" };
        using var provider = new OnnxEmbeddingProvider(Options.Create(options), NullLogger<OnnxEmbeddingProvider>.Instance);
        provider.Status().Should().Be(new EmbeddingStatus(EmbeddingState.Ready));
        var expected = provider.EmbedPassages(["a passage"])[0];
        using var stop = new CancellationTokenSource();
        var token = TestContext.Current.CancellationToken;

        // An unload asked all the time (a delay of nothing), against requests from three threads: whichever one wins the turn, a request is answered with the same vector.
        const int Threads = 3;
        const int RequestsPerThread = 5;
        var unloads = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                provider.UnloadIfIdle(TimeSpan.Zero);
                Thread.Sleep(20);
            }
        }, token);
        var requests = Enumerable.Range(0, Threads).Select(_ => Task.Run(() => Enumerable.Range(0, RequestsPerThread).Select(_ => provider.EmbedPassages(["a passage"])[0]).ToList(), token)).ToList();

        var answers = (await Task.WhenAll(requests)).SelectMany(list => list).ToList();
        await stop.CancelAsync();

        await unloads;
        answers.Should().HaveCount(Threads * RequestsPerThread).And.OnlyContain(vector => vector.SequenceEqual(expected));
    }

    [Fact]
    public void should_not_build_again_a_model_file_that_was_swapped_while_it_slept_and_say_so()
    {
        Assert.SkipUnless(Model is not null && OperatingSystem.IsLinux(), "No model folder given, or no symbolic links.");
        var folder = Directory.CreateTempSubdirectory("mnemarr-model-").FullName;
        try
        {
            // The real files by links: a copy of the model is 390 MB. The model file is then replaced by another one, as a writable folder allows.
            foreach (var file in Directory.EnumerateFiles(Model!))
            {
                File.CreateSymbolicLink(Path.Combine(folder, Path.GetFileName(file)), file);
            }

            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero));
            using var provider = new OnnxEmbeddingProvider(Options.Create(new EmbeddingOptions { ModelFolder = folder, ExtensionPath = "unused" }), NullLogger<OnnxEmbeddingProvider>.Instance, clock);
            provider.Status().Should().Be(new EmbeddingStatus(EmbeddingState.Ready));
            clock.Advance(TimeSpan.FromMinutes(11));
            provider.UnloadIfIdle(TimeSpan.FromMinutes(10)).Should().BeTrue();
            File.Delete(Path.Combine(folder, "model.onnx"));
            File.WriteAllText(Path.Combine(folder, "model.onnx"), "not the model");

            var embed = () => provider.EmbedPassages(["a passage"]);

            embed.Should().Throw<EmbeddingException>();
            provider.Known()!.State.Should().Be(EmbeddingState.Failed, "the health tells that the model is down");
            provider.Known()!.Reason.Should().Contain("not the one pinned");
            clock.Advance(TimeSpan.FromMinutes(6));
            File.Delete(Path.Combine(folder, "model.onnx"));
            File.CreateSymbolicLink(Path.Combine(folder, "model.onnx"), Path.Combine(Model!, "model.onnx"));
            provider.EmbedPassages(["a passage"]).Should().ContainSingle("the file is back, and the model is tried again after a while");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void should_say_that_the_model_is_down_when_its_file_is_gone_while_it_slept()
    {
        Assert.SkipUnless(Model is not null && OperatingSystem.IsLinux(), "No model folder given, or no symbolic links.");
        var folder = Directory.CreateTempSubdirectory("mnemarr-model-").FullName;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Model!))
            {
                File.CreateSymbolicLink(Path.Combine(folder, Path.GetFileName(file)), file);
            }

            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero));
            using var provider = new OnnxEmbeddingProvider(Options.Create(new EmbeddingOptions { ModelFolder = folder, ExtensionPath = "unused" }), NullLogger<OnnxEmbeddingProvider>.Instance, clock);
            provider.Status();
            clock.Advance(TimeSpan.FromMinutes(11));
            provider.UnloadIfIdle(TimeSpan.FromMinutes(10)).Should().BeTrue();
            File.Delete(Path.Combine(folder, "model.onnx"));

            var embed = () => provider.EmbedPassages(["a passage"]);

            embed.Should().Throw<EmbeddingException>();
            provider.Known()!.Should().Be(new EmbeddingStatus(EmbeddingState.Failed, "The file model.onnx of the model is not in the model folder."));
            embed.Should().Throw<EmbeddingException>("the next request does not try again before the delay");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
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
