using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Mnemarr.Api.V1.Collectors;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;

namespace Mnemarr.Api.V1.Mcp;

/// <summary>Who is calling a tool: what they may reach, the name their changes are written under, the permissions they hold, and the address they reached Mnemarr at.</summary>
public sealed record McpCaller(SourceScope Scope, string Name, Func<string, bool> Has, string Server = "");

/// <summary>A tool offered to assistants: its name, what it is for, the shape of its arguments, the permission it needs, and whether it writes.</summary>
public sealed record McpTool(string Name, string Description, JsonObject Schema, string Permission, bool Writes);

/// <summary>
/// The tools of the MCP server (F-MCP-01, ADR 0023): the same services as the REST API, under the same permissions and the same scope of
/// the caller's key, so that an assistant can do nothing there that the API would refuse. Reading and searching need the permissions that
/// say so; the tools that write need <c>notes.write</c> and are offered only when the operator turned them on (<see cref="McpOptions.WriteTools"/>).
/// </summary>
public sealed class McpTools
{
    private const int MaxListed = 200;
    private const string Str = "string";
    private const string ContentKey = "content";
    private const string VersionKey = "version";
    private const string PathKey = "path";
    private const string SourceKey = "source";
    private static readonly (string, string, string, bool) SourceArg = (SourceKey, Str, "A source name.", true);
    private static readonly (string, string, string, bool) OptionalSourceArg = (SourceKey, Str, "A source name.", false);
    private static readonly (string, string, string, bool) PathArg = (PathKey, Str, "The path of the note.", true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SourceCatalog _catalog;
    private readonly NoteSearch _search;
    private readonly NoteReader _reader;
    private readonly NoteWriter _writer;
    private readonly IOptions<McpOptions> _options;
    private readonly EnrolmentService _enrolments;
    private readonly InstallCommands _installs;

    public McpTools(SourceCatalog catalog, NoteSearch search, NoteReader reader, NoteWriter writer, IOptions<McpOptions> options, EnrolmentService enrolments, InstallCommands installs)
    {
        _search = search;
        _enrolments = enrolments;
        _installs = installs;
        _catalog = catalog;
        _reader = reader;
        _writer = writer;
        _options = options;
    }

    private static readonly McpTool[] All =
    [
        new("list_sources", "The sources of notes you may use: their name, kind, whether notes can be written in them, and how many notes they hold.", Schema(), Permissions.NotesRead, false),
        new("search", "Searches the notes by words and, when the host has the model, by meaning (the question may use other words than the note; the best matches first, each with how it matched: keywords, meaning or both). Optionally within one source or one topic (the first folder of a note).",
            Schema(("query", Str, "The words to look for.", true), OptionalSourceArg, ("topic", Str, "A topic: the first folder of a note.", false), ("limit", "integer", "At most this many hits (1 to 25, default 10).", false)),
            Permissions.Search, false),
        new("list_notes", "Lists notes (source, path, title), of one source or carrying one tag.", Schema(OptionalSourceArg, ("tag", Str, "A tag, without #.", false)), Permissions.NotesRead, false),
        new("read_note", "Reads a note: its whole text, its tags, the notes that link to it, and its version (send it back to change the note).",
            Schema(SourceArg, (PathKey, Str, "The path of the note in its source, such as Topic/Note.md.", true)), Permissions.NotesRead, false),
        new("list_tags", "The tags of the notes, most used first.", Schema(), Permissions.NotesRead, false),
        new("create_note", "Creates a note. The path ends in .md; folders are created. The text is the whole file, front matter included.",
            Schema((SourceKey, Str, "A source name (it must be open for writing).", true), (PathKey, Str, "Where the note goes, such as Topic/Note.md.", true), (ContentKey, Str, "The whole text of the note.", true)), Permissions.NotesWrite, true),
        new("update_note", "Replaces the whole text of a note. Give the version you read: a note that changed since is refused, so that nothing is overwritten.",
            Schema(SourceArg, PathArg, (ContentKey, Str, "The whole new text.", true), (VersionKey, Str, "The version of the note you read.", true)), Permissions.NotesWrite, true),
        new("append_to_note", "Adds text at the end of a note, whatever its version: to jot something down without reading the note first.",
            Schema(SourceArg, PathArg, ("text", Str, "The text to add.", true)), Permissions.NotesWrite, true),
        new("rename_note", "Moves a note, and rewrites the links that lead to it in the notes of the same source.",
            Schema(SourceArg, ("from", Str, "The current path.", true), ("to", Str, "The new path.", true)), Permissions.NotesWrite, true),
        new("delete_note", "Removes a note (a Git source keeps it in its history; a plain folder keeps it in a hidden trash folder). Give the version you read to be sure it is the note you mean.",
            Schema(SourceArg, PathArg, (VersionKey, Str, "The version you read.", false)), Permissions.NotesWrite, true),
        new("request_collector_enrolment", "Asks for a machine to join Mnemarr, so that its conversations are collected. It returns a one-time code and the command to run on that machine; the code works only after a person who manages the collectors has approved the request in Mnemarr (the Machines page), and then for 15 minutes. Nothing is run on the machine by Mnemarr.",
            Schema(("machine", Str, "The name of the machine: letters, digits, dots, hyphens, underscores; the key it is given is bound to it.", true), ("kind", Str, "always-on for a server that runs all the time, intermittent for a workstation that sleeps (default).", false)),
            Permissions.CollectorsRequest, false),
    ];

    /// <summary>The tools this caller is offered: what their permissions allow, the writing ones only if the operator turned them on.</summary>
    public IEnumerable<McpTool> Available(McpCaller caller) => All.Where(tool => caller.Has(tool.Permission) && (!tool.Writes || _options.Value.WriteTools));

    public async Task<JsonNode> CallAsync(string name, JsonElement arguments, McpCaller caller, CancellationToken cancellationToken)
    {
        // A tool that is not offered does not exist for the caller, whatever its name says.
        var tool = Available(caller).FirstOrDefault(known => known.Name == name) ?? throw new RejectedException(Problem.NotFound, $"There is no tool named '{name}' for you.");
        var actor = new NoteActor(caller.Scope, caller.Name);
        object result = tool.Name switch
        {
            "list_sources" => ListSources(caller),
            "search" => Search(arguments, caller.Scope),
            "list_notes" => ListNotes(arguments, caller.Scope),
            "read_note" => Read(arguments, caller),
            "list_tags" => _reader.Tags(caller.Scope),
            "create_note" => await _writer.CreateAsync(actor, SourceId(arguments, caller.Scope), Text(arguments, PathKey), Text(arguments, ContentKey), cancellationToken),
            "update_note" => await _writer.UpdateAsync(actor, SourceId(arguments, caller.Scope), Text(arguments, PathKey), Text(arguments, ContentKey), Text(arguments, VersionKey), cancellationToken),
            "append_to_note" => await _writer.AppendAsync(actor, SourceId(arguments, caller.Scope), Text(arguments, PathKey), Text(arguments, "text"), cancellationToken),
            "rename_note" => await _writer.RenameAsync(actor, SourceId(arguments, caller.Scope), Text(arguments, "from"), Text(arguments, "to"), cancellationToken),
            "request_collector_enrolment" => RequestEnrolment(arguments, caller),
            "delete_note" => DeleteResult(await _writer.DeleteAsync(actor, SourceId(arguments, caller.Scope), Text(arguments, PathKey), Optional(arguments, VersionKey), cancellationToken)),
            _ => throw new RejectedException(Problem.NotFound, "There is no such tool."),
        };
        return JsonSerializer.SerializeToNode(result, Json) ?? new JsonObject();
    }

    private object RequestEnrolment(JsonElement arguments, McpCaller caller)
    {
        var machine = Text(arguments, "machine");
        var kind = MachineKind.Intermittent;
        if (Optional(arguments, "kind") is { } named && !MachineKinds.TryParse(named, out kind))
        {
            throw new RejectedException(Problem.Invalid, "The kind of a machine is always-on or intermittent.");
        }

        var created = _enrolments.Request(machine, kind, approveNow: false, caller.Name);
        var server = string.IsNullOrEmpty(caller.Server) ? "<the address of Mnemarr>" : caller.Server;
        var install = _installs.For(server, created.Code, machine);
        return new
        {
            requestId = created.Enrolment.Id,
            machine,
            kind = MachineKinds.Name(kind),
            state = "pending",
            code = created.Code,
            install = install.Commands.Select(command => new { system = command.System, label = command.Label, command = command.Command }),
            insecure = install.Insecure,
            joinOnly = EnrolCommand.For(server, created.Code, machine, install.Insecure),
            next = "A person who manages the collectors must approve this request in Mnemarr (the Machines page). Then run the install line for the system of the machine, in a terminal of that machine, as the user whose conversations it will read: it downloads the program from Mnemarr, checks it, and joins the machine. The code works for 15 minutes and once. Tell the person which machine and which code. If insecure is true, this Mnemarr is served without HTTPS: say so to the person.",
        };
    }

    private static object DeleteResult(GitPublish? published) => new { removed = true, git = published };

    private object ListSources(McpCaller caller) =>
        _catalog.List(caller.Scope).Select(source => new { name = source.Name, kind = source.Kind.ToString(), writable = NoteRights.Evaluate(source.Kind, source.Mode, caller.Has(Permissions.NotesWrite)).CanWrite, notes = _search.Counts(source.Id).Notes }).ToList();

    private object Search(JsonElement arguments, SourceScope scope)
    {
        var limit = Math.Clamp(Integer(arguments, "limit") ?? 10, 1, 25);
        Guid? source = Optional(arguments, SourceKey) is null ? null : SourceId(arguments, scope);
        return _search.Search(new SearchRequest(Text(arguments, "query"), source, Optional(arguments, "topic"), limit), scope)
            .Select(hit => new { source = hit.SourceName, path = hit.Path, title = hit.Title, heading = hit.Heading, snippet = hit.Snippet, match = hit.Match }).ToList();
    }

    private object ListNotes(JsonElement arguments, SourceScope scope)
    {
        Guid? source = Optional(arguments, SourceKey) is null ? null : SourceId(arguments, scope);
        var listing = _reader.List(scope, source, Optional(arguments, "tag"));
        return new { total = listing.Total, notes = listing.Notes.Take(MaxListed).Select(note => new { source = note.SourceName, path = note.Path, title = note.Title }).ToList() };
    }

    private object Read(JsonElement arguments, McpCaller caller)
    {
        var note = _reader.Read(SourceId(arguments, caller.Scope), Text(arguments, PathKey), caller.Scope);
        return new
        {
            source = note.Detail.Note.SourceName,
            path = note.Detail.Note.Path,
            title = note.Detail.Note.Title,
            tags = note.Detail.Tags,
            version = note.Version,
            kind = note.Kind.ToString(),
            writable = NoteRights.Evaluate(note.Kind, note.Mode, caller.Has(Permissions.NotesWrite)).CanWrite,
            content = note.Raw,
            linkedFrom = note.Detail.Backlinks.Select(backlink => new { source = backlink.SourceName, path = backlink.Path }).ToList(),
        };
    }

    private Guid SourceId(JsonElement arguments, SourceScope scope)
    {
        var name = Text(arguments, SourceKey);
        var sources = _catalog.List(scope);
        var found = sources.FirstOrDefault(source => string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (Guid.TryParse(name, out var id) ? sources.FirstOrDefault(source => source.Id == id) : null);
        return found?.Id ?? throw new RejectedException(Problem.NotFound, "There is no such source: list_sources says which there are.");
    }

    private static string Text(JsonElement arguments, string name) =>
        Optional(arguments, name) ?? throw new RejectedException(Problem.Invalid, $"The argument '{name}' is required.");

    private static string? Optional(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Integer(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>The JSON Schema of the arguments of a tool: an object with properties of a type each, some required.</summary>
    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
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
}
