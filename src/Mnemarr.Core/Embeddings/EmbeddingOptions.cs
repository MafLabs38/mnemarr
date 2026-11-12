using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Embeddings;

/// <summary>
/// Settings of the search by meaning (ADR 0034, from the <c>Embeddings</c> section). Without a <see cref="ModelFolder"/> the feature is off: search stays
/// by keywords and nothing is computed.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>The model, by identifier. Only the listed models are supported (<see cref="KnownModels"/>).</summary>
    public string Model { get; set; } = KnownModels.Default;

    /// <summary>The folder that holds the files of the model (<c>model.onnx</c>, <c>tokenizer.json</c>). Empty: the search by meaning is off.</summary>
    public string? ModelFolder { get; set; }

    /// <summary>The loadable <c>sqlite-vec</c> library, at a path Mnemarr is given by its operator or its image, never by input.</summary>
    public string? ExtensionPath { get; set; }

    /// <summary>The CPU threads the model may use. The first run over a long history is slow; the rest of the host must stay usable.</summary>
    public int Threads { get; set; } = 2;

    /// <summary>The longest a passage may be, in tokens: the rest is not read by the model.</summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>How many passages are computed together.</summary>
    public int BatchSize { get; set; } = 8;

    /// <summary>
    /// How long the indexer waits, in seconds, when there is nothing to compute, before it looks again. It does not poll for the work: the index tells it when a chunk is new
    /// (ADR 0039), and this is the safety net for a change it was not told of.
    /// </summary>
    public int IdleSeconds { get; set; } = 300;

    /// <summary>How many days a computed vector is kept after it was last used, so that a passage that comes back (a note moved, renamed, indexed again) is not computed again.</summary>
    public int CacheDays { get; set; } = 30;

    /// <summary>
    /// The cosine distance beyond which a passage is not a match by meaning (0 is the same, 2 is opposite): the nearest passages of a question that nothing answers are
    /// not shown as answers. Empty: the one measured for the model (<see cref="KnownModel.MaxDistance"/>).
    /// </summary>
    public double? MaxDistance { get; set; }

    /// <summary>Where the model runs on this host (ADR 0040): the CPU, a CUDA GPU, or the GPU when it works and the CPU when it does not.</summary>
    public EmbeddingDevice Device { get; set; } = EmbeddingDevice.Cpu;

    /// <summary>The GPU to use when <see cref="Device"/> is not the CPU.</summary>
    public int GpuDeviceId { get; set; }

    /// <summary>
    /// The most video memory the model may take, in megabytes: by default the engine's memory pool grows without a limit, and the GPU may be shared with something else
    /// (a media server that transcodes, for one).
    /// </summary>
    public int GpuMemoryMb { get; set; } = 1024;

    /// <summary>A server that computes the vectors for this host (ADR 0040). Empty: they are computed here.</summary>
    public RemoteEmbeddingOptions Remote { get; set; } = new();

    /// <summary>Whether the feature is wanted at all: a model folder is given.</summary>
    public bool Wanted => !string.IsNullOrWhiteSpace(ModelFolder);
}

/// <summary>Where the model runs on the host that holds it.</summary>
public enum EmbeddingDevice
{
    Cpu,
    Cuda,

    /// <summary>The GPU when the engine can use it, the CPU when it cannot: the search goes on either way.</summary>
    Auto,
}

/// <summary>
/// A server that computes the vectors (ADR 0040): a small program that holds the model next to a GPU. Mnemarr keeps its logic and its data; the server only turns texts into
/// vectors. When it does not answer, Mnemarr computes on its own CPU and tries the server again after a while.
/// </summary>
public sealed class RemoteEmbeddingOptions
{
    /// <summary>The address of the server. Empty: no server.</summary>
    public string? Url { get; set; }

    /// <summary>A file with the key the server was given, one line: it is a secret, never a value in the configuration.</summary>
    public string? KeyFile { get; set; }

    /// <summary>The longest Mnemarr waits for an answer before it counts the server as down.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>How long Mnemarr computes on its own CPU after the server failed, before it asks the server again.</summary>
    public int RetryAfterSeconds { get; set; } = 60;

    /// <summary>Allows <c>http</c> to an address that is not this host: for a server on a network the operator trusts. The key is sent in the clear.</summary>
    public bool AllowInsecureHttp { get; set; }

    public bool Wanted => !string.IsNullOrWhiteSpace(Url);
}

public sealed class EmbeddingOptionsValidator : IValidateOptions<EmbeddingOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        if (!KnownModels.TryGet(options.Model, out _))
        {
            return ValidateOptionsResult.Fail($"Embeddings:Model (MNEMARR__EMBEDDINGS__MODEL) must be one of: {string.Join(", ", KnownModels.Identifiers)}.");
        }

        if (options.Wanted && string.IsNullOrWhiteSpace(options.ExtensionPath))
        {
            return ValidateOptionsResult.Fail("Embeddings:ExtensionPath (MNEMARR__EMBEDDINGS__EXTENSIONPATH) is required when a model folder is given: it is the sqlite-vec library.");
        }

        if (RemoteProblem(options) is { } remote)
        {
            return ValidateOptionsResult.Fail(remote);
        }

        if (RangeProblem(options) is { } range)
        {
            return ValidateOptionsResult.Fail(range);
        }

        return options.MaxDistance is null or (> 0 and <= 2)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Embeddings:MaxDistance must be above 0 and at most 2.");
    }

    /// <summary>What is out of range among the settings of the model (the batch, the threads, the GPU), or null. Shared with the embedding server.</summary>
    public static string? RangeProblem(EmbeddingOptions options)
    {
        if (options.GpuMemoryMb is < 256 or > 262_144 || options.GpuDeviceId is < 0 or > 64)
        {
            return "Embeddings: GpuMemoryMb is 256 to 262144 and GpuDeviceId 0 to 64.";
        }

        return options.Threads is < 1 or > 64 || options.BatchSize is < 1 or > 64 || options.MaxTokens is < 16 or > 8192 || options.IdleSeconds is < 1 or > 3600 || options.CacheDays is < 1 or > 3650
            ? "Embeddings: Threads is 1 to 64, BatchSize 1 to 64, MaxTokens 16 to 8192, IdleSeconds 1 to 3600 and CacheDays 1 to 3650."
            : null;
    }

    private static string? RemoteProblem(EmbeddingOptions options)
    {
        var remote = options.Remote;
        if (!remote.Wanted)
        {
            return null;
        }

        if (!options.Wanted)
        {
            return "Embeddings:Remote:Url needs Embeddings:ModelFolder as well: the model of this host is what computes when the server does not answer.";
        }

        if (!Uri.TryCreate(remote.Url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0)
        {
            return "Embeddings:Remote:Url (MNEMARR__EMBEDDINGS__REMOTE__URL) must be an absolute http or https address, with no credentials in it.";
        }

        if (address.Scheme == "http" && !address.IsLoopback && !remote.AllowInsecureHttp)
        {
            return "Embeddings:Remote:Url must be https: http is for this host only, or for a network you trust with Embeddings:Remote:AllowInsecureHttp (the key is then sent in the clear).";
        }

        if (string.IsNullOrWhiteSpace(remote.KeyFile))
        {
            return "Embeddings:Remote:KeyFile (MNEMARR__EMBEDDINGS__REMOTE__KEYFILE) is required with a server: the file that holds its key.";
        }

        return remote.TimeoutSeconds is < 1 or > 300 || remote.RetryAfterSeconds is < 5 or > 3600
            ? "Embeddings:Remote: TimeoutSeconds is 1 to 300 and RetryAfterSeconds 5 to 3600."
            : null;
    }
}
