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
public sealed partial class OnnxEmbeddingProvider : IEmbeddingProvider, IIdleUnload, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OnnxEmbeddingProvider> _logger;
    private const string CudaDevice = "cuda";
    private const string ShrinkageEntry = "memory.enable_memory_arena_shrinkage";
    private const string ModelFile = "model.onnx";
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    // A question waits at most this long for the model's turn: a batch of the indexer takes about a second, and a question that cannot have its turn is answered by its words.
    private static readonly TimeSpan QuestionWaits = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Lock _run = new();
    private readonly TimeProvider _clock;
    private volatile EmbeddingStatus? _status;
    private DateTimeOffset _checkedAt;
    private volatile InferenceSession? _session;
    private ByteLevelBpeTokenizer? _tokenizer;
    private volatile string _device = "cpu";
    private long _lastUseTicks;

    public OnnxEmbeddingProvider(IOptions<EmbeddingOptions> options, ILogger<OnnxEmbeddingProvider> logger, TimeProvider? clock = null)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public string ModelId => _options.Model;

    public int Dimension => KnownModels.TryGet(_options.Model, out var model) ? model.Dimension : 0;

    /// <summary>Where the model runs once it is loaded: <c>cpu</c> or <c>cuda</c>.</summary>
    public string Device => _device;

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

    public float[] EmbedQuery(string text) => Embed([text], QuestionWaits)[0];

    public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts) => Embed(texts, null);

    /// <summary>
    /// Lets go of the model when nobody asked for <paramref name="idle"/> (ADR 0045): the engine keeps the video memory it took for as long as its session lives, and a GPU is shared.
    /// The files stay verified and the tokenizer stays, so the next request builds the session again, in a few seconds. Returns whether it unloaded.
    /// </summary>
    public bool UnloadIfIdle(TimeSpan idle)
    {
        if (_session is null || IdleFor() < idle)
        {
            return false;
        }

        // A run in progress is not idle: the lock is not waited for, the next pass asks again.
        if (!_run.TryEnter(TimeSpan.Zero))
        {
            return false;
        }

        try
        {
            if (_session is null || IdleFor() < idle)
            {
                return false;
            }

            _session.Dispose();
            _session = null;
            LogUnloaded(idle.TotalMinutes);
            return true;
        }
        finally
        {
            _run.Exit();
        }
    }

    public void Dispose()
    {
        // The lock of the runs and of the unload: a session is disposed once, and not under a run.
        lock (_run)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private TimeSpan IdleFor() => _clock.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref _lastUseTicks), TimeSpan.Zero);

    private void Touch() => Interlocked.Exchange(ref _lastUseTicks, _clock.GetUtcNow().UtcTicks);

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
            _session = CreateSession(ModelPath);
            Touch();
            LogLoaded(model.Id, _device, _options.Threads);
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

    // The GPU when it is asked for and the engine can use it (ADR 0040); with Auto, the CPU when it cannot, because the search must go on whatever the machine has. The memory the
    // model may take is capped: the engine's pool grows without a limit by default, and the GPU may be shared.
    private InferenceSession CreateSession(string modelPath)
    {
        if (_options.Device != EmbeddingDevice.Cpu)
        {
            try
            {
                using var gpu = Settings();
                using var cuda = new OrtCUDAProviderOptions();
                cuda.UpdateOptions(new Dictionary<string, string>
                {
                    ["device_id"] = _options.GpuDeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["gpu_mem_limit"] = (_options.GpuMemoryMb * 1024L * 1024L).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["arena_extend_strategy"] = "kSameAsRequested",
                });
                gpu.AppendExecutionProvider_CUDA(cuda);
                var session = new InferenceSession(modelPath, gpu);
                _device = CudaDevice;
                return session;
            }
            catch (Exception ex) when (_options.Device == EmbeddingDevice.Auto && ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                LogGpuUnavailable(ex);
            }
        }

        using var cpu = Settings();
        _device = "cpu";
        return new InferenceSession(modelPath, cpu);
    }

    private SessionOptions Settings() => new() { IntraOpNumThreads = _options.Threads, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };

    private string ModelPath => Path.Combine(_options.ModelFolder!, ModelFile);

    private static EmbeddingStatus Failed(string reason) => new(EmbeddingState.Failed, reason);

    private float[][] Embed(IReadOnlyList<string> texts, TimeSpan? waits)
    {
        Touch();
        if (Status().State != EmbeddingState.Ready)
        {
            throw new EmbeddingException("The model is not loaded.");
        }

        try
        {
            return Compute(texts, waits);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or System.Text.RegularExpressions.RegexMatchTimeoutException or ArgumentException or KeyNotFoundException)
        {
            // The engine and the tokenizer fail in their own ways; the callers know one.
            throw new EmbeddingException("The model failed to read a passage.", ex);
        }
    }

    private float[][] Compute(IReadOnlyList<string> texts, TimeSpan? waits)
    {
        var results = new float[texts.Count][];
        // Longest last, so that a batch pads little; the answers go back in the order of the texts.
        var order = Enumerable.Range(0, texts.Count).Select(index => (Index: index, Ids: _tokenizer!.Encode(texts[index], _options.MaxTokens))).OrderBy(item => item.Ids.Length).ToList();
        for (var from = 0; from < order.Count; from += _options.BatchSize)
        {
            var batch = order.Skip(from).Take(_options.BatchSize).ToList();
            var vectors = Run(batch.Select(item => item.Ids).ToList(), waits);
            for (var index = 0; index < batch.Count; index++)
            {
                results[batch[index].Index] = vectors[index];
            }
        }

        return results;
    }

    private float[][] Run(IReadOnlyList<int[]> batch, TimeSpan? waits)
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
        // One run at a time: the threads of the model are the budget. The indexer waits for its turn; a question waits a little, and then is answered without the model.
        var entered = waits is { } wait ? _run.TryEnter(wait) : EnterAlways();
        if (!entered)
        {
            throw new EmbeddingException("The model is busy.");
        }

        try
        {
            // The session is built again here when it was let go of while nobody asked (ADR 0045); the lock is the one the unload takes, so the two never meet.
            var session = _session ?? Rebuild();
            _session = session;
            var vectors = Infer(session, inputs, batch.Count);
            Touch();
            return vectors;
        }
        finally
        {
            _run.Exit();
        }
    }

    // The model builds again after it was let go of (ADR 0045). The file is hashed again: it is read again from the folder, and a folder that is not read-only may have been written to since the start.
    // A model that cannot be built again is a model that is down, like one that cannot be loaded: the health says so, and it is tried again after a while.
    private InferenceSession Rebuild()
    {
        string problem;
        try
        {
            if (!KnownModels.TryGet(_options.Model, out var model) || !model.Files.TryGetValue(ModelFile, out var expected))
            {
                problem = $"The model '{_options.Model}' is not one that is supported.";
            }
            else if (!string.Equals(Sha256Of(ModelPath), expected, StringComparison.Ordinal))
            {
                problem = $"The file {ModelFile} is not the one pinned for {model.Id} (revision {model.Revision}): it is not loaded.";
            }
            else
            {
                var session = CreateSession(ModelPath);
                Touch();
                return session;
            }
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LogLoadFailed(ex);
            problem = "The model could not be loaded: see the log of Mnemarr.";
        }

        lock (_gate)
        {
            _status = Failed(problem);
            _checkedAt = _clock.GetUtcNow();
        }

        throw new EmbeddingException("The model could not be built again.");
    }

    private float[][] Infer(InferenceSession session, List<NamedOnnxValue> inputs, int rows)
    {
        using var run = new RunOptions();
        if (_device == CudaDevice && _options.GpuShrinkAfterRun)
        {
            run.AddRunConfigEntry(ShrinkageEntry, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"gpu:{_options.GpuDeviceId}"));
        }

        using var outputs = session.Run(inputs, session.OutputNames, run);
        var hidden = outputs[0].AsTensor<float>();
        var vectors = new float[rows][];
        for (var row = 0; row < rows; row++)
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

    private bool EnterAlways()
    {
        _run.Enter();
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The model {Model} is loaded, on the {Device}, with {Threads} CPU threads.")]
    private partial void LogLoaded(string model, string device, int threads);

    [LoggerMessage(Level = LogLevel.Information, Message = "Nobody asked for {Minutes} minutes: the model is let go of, and loaded again at the next request.")]
    private partial void LogUnloaded(double minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The GPU cannot be used by the engine: the model runs on the CPU.")]
    private partial void LogGpuUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The model could not be loaded.")]
    private partial void LogLoadFailed(Exception exception);
}
