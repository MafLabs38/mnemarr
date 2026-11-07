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
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Lock _run = new();
    private readonly TimeProvider _clock;
    private volatile EmbeddingStatus? _status;
    private DateTimeOffset _checkedAt;
    private InferenceSession? _session;
    private ByteLevelBpeTokenizer? _tokenizer;

    public OnnxEmbeddingProvider(IOptions<EmbeddingOptions> options, ILogger<OnnxEmbeddingProvider> logger, TimeProvider? clock = null)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public string ModelId => _options.Model;

    public int Dimension => KnownModels.TryGet(_options.Model, out var model) ? model.Dimension : 0;

    public double DefaultMaxDistance => KnownModels.TryGet(_options.Model, out var model) ? model.MaxDistance : 0;

    public EmbeddingStatus Status()
    {
        if (!_options.Wanted)
        {
            return new EmbeddingStatus(EmbeddingState.Off);
        }

        if (_status is { } known && !RetryDue(known))
        {
            return known;
        }

        // One load at a time, and the others wait for its answer: only a pass of the indexer calls this, so that no request and no probe waits for the model.
        lock (_gate)
        {
            if (_status is not { } current || RetryDue(current))
            {
                var loaded = Load();
                _checkedAt = _clock.GetUtcNow();
                _status = loaded;
            }

            return _status;
        }
    }

    /// <summary>What <see cref="Status"/> answered, without loading anything and without waiting for a load that is going on.</summary>
    public EmbeddingStatus? Known() => _options.Wanted ? _status : new EmbeddingStatus(EmbeddingState.Off);

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

    // A model that could not be loaded is tried again after a while: a volume that was mounted late, a file that was put back.
    private bool RetryDue(EmbeddingStatus status) => status.State == EmbeddingState.Failed && _clock.GetUtcNow() - _checkedAt >= RetryAfter;

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever goes wrong loading an engine that is native code (a library that is missing, a file that is damaged) is a reason the feature is down, not a crash.
            // The detail is for the log: the people who ask where the search is at are not told paths of the host.
            LogLoadFailed(ex);
            return Failed("The model could not be loaded: see the log of Mnemarr.");
        }
    }

    private static EmbeddingStatus Failed(string reason) => new(EmbeddingState.Failed, reason);

    private float[][] Embed(IReadOnlyList<string> texts)
    {
        if (Status().State != EmbeddingState.Ready)
        {
            throw new EmbeddingException("The model is not loaded.");
        }

        try
        {
            return Compute(texts);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or System.Text.RegularExpressions.RegexMatchTimeoutException or ArgumentException or KeyNotFoundException)
        {
            // The engine and the tokenizer fail in their own ways; the callers know one.
            throw new EmbeddingException("The model failed to read a passage.", ex);
        }
    }

    private float[][] Compute(IReadOnlyList<string> texts)
    {
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
