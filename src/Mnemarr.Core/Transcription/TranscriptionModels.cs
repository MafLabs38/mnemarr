using System.Text;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Declarative;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mnemarr.Core.Transcription;

/// <summary>A model the dictation can use: the name the service knows, a label for a person, and its price per minute in dollars when it is known.</summary>
public sealed record TranscriptionModel(string Id, string Label, decimal? PricePerMinute);

/// <summary>The models to choose from, and why the file could not be read when it could not (the provider's own list then applies).</summary>
public sealed record TranscriptionCatalog(IReadOnlyList<TranscriptionModel> Models, bool FromFile, string? Error);

/// <summary>
/// The catalog of models (ADR 0047): a YAML file of the deployment (<c>Transcription:ModelsFile</c>), read again when it changes, or the list of the selected provider. Model names change every few
/// months: a new one is a line of the file, not a release. The file is read strictly, as the configuration document is: one document, no anchors, no tags, no unknown key.
/// </summary>
public sealed class TranscriptionModels
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxModels = 50;

    private static readonly IDeserializer Reader = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
    private readonly IOptions<TranscriptionOptions> _options;
    private readonly IEnumerable<ITranscriptionProvider> _providers;
    private readonly Lock _gate = new();
    private (string? Path, DateTime Written, long Length, TranscriptionCatalog Catalog)? _cache;

    /// <summary>The provider these settings select, or null.</summary>
    public ITranscriptionProvider? Provider(TranscriptionOptions options) => TranscriptionProviders.Find(_providers, options.Provider);

    public TranscriptionModels(IOptions<TranscriptionOptions> options, IEnumerable<ITranscriptionProvider> providers)
    {
        _options = options;
        _providers = providers;
    }

    /// <summary>
    /// Without a file, a model the deployment names (<c>Transcription:Model</c>) that the provider's list does not have is part of the catalog too, first: a local server
    /// reached by changing the address and naming its model works without a file.
    /// </summary>
    private IReadOnlyList<TranscriptionModel> WithDeploymentModel(IReadOnlyList<TranscriptionModel> models)
    {
        var named = _options.Value.Model?.Trim();
        return !IsModelId(named) || models.Any(model => model.Id == named) ? models : [new TranscriptionModel(named!, named!, null), .. models];
    }

    /// <summary>The selected provider's own models; OpenAI's while dictation is off, so that the page has a list to show (nothing is sent then).</summary>
    public IReadOnlyList<TranscriptionModel> Defaults =>
        TranscriptionProviders.Find(_providers, _options.Value.Provider)?.DefaultModels is { Count: > 0 } models ? models : OpenAiCompatibleTranscriptionProvider.Models;

    /// <summary>The catalog as it is now: the file is read again only when its time or size changed.</summary>
    public TranscriptionCatalog Current()
    {
        var path = _options.Value.ModelsFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new TranscriptionCatalog(WithDeploymentModel(Defaults), false, null);
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return new TranscriptionCatalog(Defaults, false, "The file of the transcription models cannot be read: the provider's own list applies.");
        }

        lock (_gate)
        {
            if (_cache is { } cached && cached.Path == path && cached.Written == file.LastWriteTimeUtc && cached.Length == file.Length)
            {
                return cached.Catalog;
            }

            var catalog = Read(file, Defaults);
            _cache = (path, file.LastWriteTimeUtc, file.Length, catalog);
            return catalog;
        }
    }

    private static TranscriptionCatalog Read(FileInfo file, IReadOnlyList<TranscriptionModel> defaults)
    {
        TranscriptionCatalog Fallback(string why) => new(defaults, false, $"The file of the transcription models cannot be used ({why}): the provider's own list applies.");

        if (file.Length > MaxBytes)
        {
            return Fallback($"it is larger than {MaxBytes / 1024} KB");
        }

        string text;
        try
        {
            text = File.ReadAllText(file.FullName, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fallback("it cannot be read");
        }

        var (models, error) = Parse(text);
        return error is null ? new TranscriptionCatalog(models, true, null) : Fallback(error);
    }

    /// <summary>The models of a file, or why they cannot be used, in a sentence that gives no path.</summary>
    internal static (IReadOnlyList<TranscriptionModel> Models, string? Error) Parse(string text)
    {
        ModelsFile? document;
        try
        {
            ConfigurationYaml.CheckShape(text, "The file of the transcription models");
            document = Reader.Deserialize<ModelsFile?>(text);
        }
        catch (RejectedException ex)
        {
            return ([], ex.Message);
        }
        catch (YamlException ex)
        {
            return ([], $"it is not valid YAML, line {ex.Start.Line}");
        }

        var entries = document?.Models;
        if (entries is not { Count: > 0 } || entries.Count > MaxModels)
        {
            return ([], $"it lists no model, or more than {MaxModels} (models: [{{ id, label, pricePerMinute }}])");
        }

        var models = new List<TranscriptionModel>();
        foreach (var entry in entries)
        {
            if (Check(entry, models) is { } problem)
            {
                return ([], problem);
            }

            models.Add(new TranscriptionModel(entry!.Id!, string.IsNullOrWhiteSpace(entry.Label) ? entry.Id! : entry.Label.Trim(), entry.PricePerMinute));
        }

        return (models, null);
    }

    private static string? Check(ModelEntry? entry, List<TranscriptionModel> before)
    {
        if (entry is null)
        {
            return "a model entry is empty";
        }

        if (!IsModelId(entry.Id))
        {
            return "a model has no id, or an id that is not 1 to 100 letters, digits and . _ : - /";
        }

        if (before.Any(model => model.Id == entry.Id))
        {
            return $"the model '{entry.Id}' is listed twice";
        }

        if (entry.Label is { Length: > 80 } || entry.Label?.Any(char.IsControl) == true)
        {
            return $"the label of '{entry.Id}' is longer than 80 characters or holds a control character";
        }

        return entry.PricePerMinute is < 0 or > 100 ? $"the price of '{entry.Id}' is not between 0 and 100" : null;
    }

    /// <summary>The characters a model's name may have: it is sent to the service as a form field and shown on a page.</summary>
    public static bool IsModelId(string? id) =>
        id is { Length: > 0 and <= 100 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-' or '/');

    /// <summary>The shape of the file, as YamlDotNet fills it (it sets the properties).</summary>
    internal sealed class ModelsFile
    {
        public List<ModelEntry?>? Models { get; set; }
    }

    internal sealed class ModelEntry
    {
        public string? Id { get; set; }

        public string? Label { get; set; }

        public decimal? PricePerMinute { get; set; }
    }
}
