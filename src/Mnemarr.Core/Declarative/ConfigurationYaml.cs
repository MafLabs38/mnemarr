using System.Text;
using Mnemarr.Core.Sources;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mnemarr.Core.Declarative;

/// <summary>
/// Reads and writes a configuration document as YAML. A document is input from outside, so it is read strictly: a bounded size, one
/// document, a bounded depth, no anchors or aliases and no tags (an alias is how a small document becomes a huge one), and a key that is
/// not known is an error and not something that is ignored.
/// </summary>
public static class ConfigurationYaml
{
    public const int MaxBytes = 256 * 1024;
    private const int MaxDepth = 8;

    private static readonly IDeserializer Reader = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    private static readonly ISerializer Writer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static ConfigurationDocument Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw Invalid($"The configuration is larger than {MaxBytes / 1024} KB.");
        }

        try
        {
            CheckShape(text);
            var document = Reader.Deserialize<ConfigurationDocument?>(text) ?? new ConfigurationDocument();
            RefuseSectionWithoutValue(text);
            return document;
        }
        catch (YamlException ex)
        {
            throw Invalid($"The configuration is not valid YAML for Mnemarr, line {ex.Start.Line}: {Innermost(ex).Message}");
        }
    }

    /// <summary>
    /// A section that is written with no value (<c>knowledge:</c>) reads as null, which is the same as a section that is not there: it would
    /// leave the sources of that section alone while looking like it describes none. Only <c>[]</c> says that a section holds nothing.
    /// </summary>
    private static void RefuseSectionWithoutValue(string text)
    {
        var raw = Reader.Deserialize<Dictionary<string, object?>?>(text);
        foreach (var section in new[] { "memory", "knowledge", "applicationDocs" })
        {
            if (raw is not null && raw.TryGetValue(section, out var value) && value is null)
            {
                throw Invalid($"'{section}' has no value: leave it out to leave that section alone, or describe it (a list can be written [] to say that it holds no source).");
            }
        }
    }

    /// <summary>The sources as a document: all three sections, so that applying it back changes nothing and leaves nothing out.</summary>
    public static string Export(IEnumerable<Source> sources)
    {
        var all = sources.OrderBy(source => source.Name, StringComparer.Ordinal).ToList();
        var memory = all.FirstOrDefault(source => source.Kind == SourceKind.Memory);
        var document = new ConfigurationDocument
        {
            Memory = memory is null ? null : Entry(memory),
            Knowledge = [.. all.Where(source => source.Kind == SourceKind.Knowledge).Select(Entry)],
            ApplicationDocs = [.. all.Where(source => source.Kind == SourceKind.ApplicationDocs).Select(Entry)],
        };
        return Writer.Serialize(document);
    }

    private static SourceEntry Entry(Source source) => new()
    {
        Name = source.Name,
        Path = source.Path,
        Mode = source.Mode.ToString(),
        Repository = source.Repository is null ? null : new RepositoryEntry { Url = source.Repository.Url, Branch = source.Repository.Branch, Credential = source.Repository.Credential },
    };

    /// <summary>Reads the events of the document, without building anything: what is not allowed is refused before it can cost anything.</summary>
    private static void CheckShape(string text)
    {
        var parser = new Parser(new StringReader(text));
        var depth = 0;
        var documents = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart when ++documents > 1:
                    throw Invalid("The configuration is one YAML document.");
                case NodeEvent { Anchor.IsEmpty: false }:
                    // An alias needs an anchor first, so refusing the anchor refuses every alias.
                    throw Invalid("The configuration has no anchors or aliases (&name, *name): write the value out.");
                case NodeEvent { Tag.IsEmpty: false }:
                    throw Invalid("The configuration has no tags (!name): a value is what it looks like.");
                case MappingStart or SequenceStart when ++depth > MaxDepth:
                    throw Invalid($"The configuration is nested more than {MaxDepth} levels deep.");
                case MappingEnd or SequenceEnd:
                    depth--;
                    break;
                default:
                    break;
            }
        }
    }

    private static Exception Innermost(Exception exception) => exception.InnerException is { } inner ? Innermost(inner) : exception;

    private static RejectedException Invalid(string message) => new(Problem.Invalid, message);
}
