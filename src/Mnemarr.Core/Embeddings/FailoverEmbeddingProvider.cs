using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Embeddings;

/// <summary>
/// The vectors come from the embedding server when it answers, and from this host's own model when it does not (ADR 0040). The indexing never waits for the GPU: a server that fails
/// is left alone for <c>RetryAfterSeconds</c> while the CPU computes, then asked again, and the next batch goes back to it when it is up. The two give the same vectors for the same
/// model (the same files, checked by hash on both sides), so what was computed by one is valid for the other, and what is kept in the cache is not tied to either.
/// </summary>
public sealed partial class FailoverEmbeddingProvider : IEmbeddingProvider
{
    private readonly IEmbeddingProvider _local;
    private readonly RemoteEmbeddingClient _remote;
    private readonly EmbeddingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FailoverEmbeddingProvider> _logger;
    private long _retryAt;
    private volatile string _device = "cpu";

    public FailoverEmbeddingProvider(IEmbeddingProvider local, RemoteEmbeddingClient remote, IOptions<EmbeddingOptions> options, ILogger<FailoverEmbeddingProvider> logger, TimeProvider? clock = null)
    {
        _local = local;
        _remote = remote;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _device = _options.Remote.Wanted ? "remote" : "cpu";
    }

    public string ModelId => _local.ModelId;

    public int Dimension => _local.Dimension;

    public double DefaultMaxDistance => _local.DefaultMaxDistance;

    public string Device => _device;

    // The model of this host is loaded only if it is needed: while the server answers, the pod does not read 390 MB and its checksum for nothing.
    public EmbeddingStatus Status() => ServerIsDue() ? new EmbeddingStatus(EmbeddingState.Ready) : _local.Status();

    public EmbeddingStatus? Known() => ServerIsDue() ? new EmbeddingStatus(EmbeddingState.Ready) : _local.Known();

    public float[] EmbedQuery(string text) => Compute([text], "query", () => [_local.EmbedQuery(text)])[0];

    public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts) => Compute(texts, "passage", () => _local.EmbedPassages(texts));

    private bool ServerIsDue() => _options.Remote.Wanted && _clock.GetTimestamp() >= Interlocked.Read(ref _retryAt);

    private IReadOnlyList<float[]> Compute(IReadOnlyList<string> texts, string kind, Func<IReadOnlyList<float[]>> onThisHost)
    {
        if (ServerIsDue())
        {
            try
            {
                var vectors = _remote.Embed(texts, kind, ModelId, Dimension);
                _device = _remote.Device ?? "remote";
                return vectors;
            }
            catch (EmbeddingException ex)
            {
                Interlocked.Exchange(ref _retryAt, _clock.GetTimestamp() + (long)(_options.Remote.RetryAfterSeconds * (double)_clock.TimestampFrequency));
                LogServerDown(_options.Remote.RetryAfterSeconds, ex);
            }
        }

        var local = onThisHost();
        _device = _local.Device;
        return local;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The embedding server did not answer: the vectors are computed on this host, and the server is asked again in {Seconds} seconds.")]
    private partial void LogServerDown(int seconds, Exception exception);
}
