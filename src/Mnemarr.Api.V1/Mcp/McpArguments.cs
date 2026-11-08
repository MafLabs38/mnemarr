using System.Text.Json;
using System.Text.Json.Nodes;
using Mnemarr.Core;

namespace Mnemarr.Api.V1.Mcp;

/// <summary>Reading the arguments of a tool call, and describing them: shared by every group of tools.</summary>
internal static class McpArguments
{
    internal static string Text(JsonElement arguments, string name) =>
        Optional(arguments, name) ?? throw new RejectedException(Problem.Invalid, $"The argument '{name}' is required.");

    internal static string? Optional(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static int? Integer(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>The JSON Schema of the arguments of a tool: an object with properties of a type each, some required.</summary>
    internal static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, type, description, _) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        schema["required"] = new JsonArray([.. properties.Where(property => property.Required).Select(property => (JsonNode)property.Name)]);
        return schema;
    }

    /// <summary>The source a call names (by name, or by id), among those the caller may touch: one that may not be touched is as absent as one that does not exist.</summary>
    internal static Guid SourceId(Mnemarr.Core.Sources.SourceCatalog catalog, JsonElement arguments, Mnemarr.Core.Accounts.SourceScope scope, string key = "source")
    {
        var name = Text(arguments, key);
        var sources = catalog.List(scope);
        var found = sources.FirstOrDefault(source => string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (Guid.TryParse(name, out var id) ? sources.FirstOrDefault(source => source.Id == id) : null);
        return found?.Id ?? throw new RejectedException(Problem.NotFound, "There is no such source: list_sources says which there are.");
    }
}
