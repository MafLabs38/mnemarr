using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mnemarr.Core.Embeddings;

/// <summary>
/// Computes embeddings on this host with ONNX Runtime (D-26, ADR 0034): no text leaves the machine. The model and its tokenizer are loaded on first use, after
/// their SHA-256 were verified against the pinned ones; a file that differs is not loaded and the reason is kept for the health page. The pooling is the
/// one of the model (the first token), and the vectors are normalised.
/// </summary>
public sealed partial class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OnnxEmbeddingProvider> _logger;
    private readonly Lock _gate = new();
    private readonly Lock _run = new();
    private EmbeddingStatus? _status;
    private InferenceSession? _session;
    private ByteLevelBpeTokenizer? _tokenizer;

    public OnnxEmbeddingProvider(IOptions<EmbeddingOptions> options, ILogger<OnnxEmbeddingProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string ModelId => _options.Model;

    public int Dimension => KnownModels.TryGet(_options.Model, out var model) ? model.Dimension : 0;

    public EmbeddingStatus Status()
    {
        if (!_options.Wanted)
        {
            return new EmbeddingStatus(EmbeddingState.Off);
        }

        lock (_gate)
        {
            return _status ??= Load();
        }
    }

    public EmbeddingStatus? Known()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public float[] EmbedQuery(string text) => Embed([text])[0];

    public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts) => Embed(texts);

    public void Dispose() => _session?.Dispose();

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Normalise(float[] vector)
    {
        var sum = 0.0;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        var norm = (float)Math.Sqrt(sum);
        if (norm > 0)
        {
            for (var index = 0; index < vector.Length; index++)
            {
                vector[index] /= norm;
            }
        }
    }

    private EmbeddingStatus Load()
    {
        try
        {
            if (!KnownModels.TryGet(_options.Model, out var model))
            {
                return Failed($"The model '{_options.Model}' is not one that is supported.");
            }

            foreach (var (file, expected) in model.Files)
            {
                var path = Path.Combine(_options.ModelFolder!, file);
                if (!File.Exists(path))
                {
                    return Failed($"The file {file} of the model is not in the model folder.");
                }

                if (!string.Equals(Sha256Of(path), expected, StringComparison.Ordinal))
                {
                    return Failed($"The file {file} is not the one pinned for {model.Id} (revision {model.Revision}): it is not loaded.");
                }
            }

            _tokenizer = ByteLevelBpeTokenizer.Load(Path.Combine(_options.ModelFolder!, "tokenizer.json"));
            using var settings = new SessionOptions { IntraOpNumThreads = _options.Threads, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _session = new InferenceSession(Path.Combine(_options.ModelFolder!, "model.onnx"), settings);
            LogLoaded(model.Id, _options.Threads);
            return new EmbeddingStatus(EmbeddingState.Ready);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OnnxRuntimeException or System.Text.Json.JsonException or KeyNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            LogLoadFailed(ex);
            return Failed($"The model could not be loaded: {ex.Message}");
        }
    }

    private static EmbeddingStatus Failed(string reason) => new(EmbeddingState.Failed, reason);

    private float[][] Embed(IReadOnlyList<string> texts)
    {
        if (Status().State != EmbeddingState.Ready)
        {
            throw new InvalidOperationException("The model is not loaded.");
        }

        var results = new float[texts.Count][];
        // Longest last, so that a batch pads little; the answers go back in the order of the texts.
        var order = Enumerable.Range(0, texts.Count).Select(index => (Index: index, Ids: _tokenizer!.Encode(texts[index], _options.MaxTokens))).OrderBy(item => item.Ids.Length).ToList();
        for (var from = 0; from < order.Count; from += _options.BatchSize)
        {
            var batch = order.Skip(from).Take(_options.BatchSize).ToList();
            var vectors = Run(batch.Select(item => item.Ids).ToList());
            for (var index = 0; index < batch.Count; index++)
            {
                results[batch[index].Index] = vectors[index];
            }
        }

        return results;
    }

    private float[][] Run(IReadOnlyList<int[]> batch)
    {
        var width = batch.Max(ids => ids.Length);
        var inputIds = new DenseTensor<long>([batch.Count, width]);
        var mask = new DenseTensor<long>([batch.Count, width]);
        for (var row = 0; row < batch.Count; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var present = column < batch[row].Length;
                inputIds[row, column] = present ? batch[row][column] : _tokenizer!.PadId;
                mask[row, column] = present ? 1 : 0;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        // One run at a time: the threads of the model are the budget, and a question must not wait behind many batches.
        lock (_run)
        {
            using var outputs = _session!.Run(inputs);
            var hidden = outputs[0].AsTensor<float>();
            var vectors = new float[batch.Count][];
            for (var row = 0; row < batch.Count; row++)
            {
                var vector = new float[hidden.Dimensions[2]];
                for (var column = 0; column < vector.Length; column++)
                {
                    vector[column] = hidden[row, 0, column];
                }

                Normalise(vector);
                vectors[row] = vector;
            }

            return vectors;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The model {Model} is loaded, on {Threads} threads.")]
    private partial void LogLoaded(string model, int threads);

    [LoggerMessage(Level = LogLevel.Error, Message = "The model could not be loaded.")]
    private partial void LogLoadFailed(Exception exception);
}
