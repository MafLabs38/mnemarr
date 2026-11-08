using System.Text.Json;
using Mnemarr.Api.V1.Collectors;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Api.V1.Mcp;

/// <summary>
/// The tools that look after Mnemarr itself (ADR 0035, D-20): its state, its sources (declare, change, forget, read again, bring a Git source up to date) and the enrolment of machines. They are
/// the same services as the REST API, under the same permissions; the ones that change something are offered only when the operator turned the writing tools on, as for notes. What asks a
/// person's approval before it runs is decided by the chat, not here: a key of an assistant calls them with the permissions of its role, nothing more.
/// </summary>
public sealed class McpAdminTools
{
    private const string Str = "string";
    private const string SourceKey = "source";
    private const string NameKey = "name";
    private const string ModeKey = "mode";
    private const string PathKey = "path";
    private const string ModeHelp = "ReadOnly or ReadWrite.";

    private static readonly (string, string, string, bool) SourceArg = (SourceKey, Str, "A source name.", true);
    private static readonly (string, string, string, bool)[] RepositoryArgs =
    [
        ("repository_url", Str, "The address (https) of a Git repository: the source is then its checkout, kept in step by pull and push.", false),
        ("repository_branch", Str, "Its branch (the default one when empty).", false),
        ("repository_credential", Str, "The name of the credential the host holds for it, never a secret.", false),
    ];

    private static readonly McpTool[] Tools =
    [
        new("get_status", "The state of Mnemarr: its version, how long it has run, how many sources it holds and where the search by meaning is at (off, starting, ready with the passages read of those that exist, or failed).", McpArguments.Schema(), Permissions.SystemRead, false),
        new("create_source", "Declares a source of notes: a folder that Mnemarr may use (list_sources shows the existing ones), optionally the checkout of a Git repository, so that notes written in it are committed and pushed.",
            McpArguments.Schema(new[] { (NameKey, Str, "The name of the source.", true), ("kind", Str, "Knowledge (a vault of notes such as courses), Memory (the memory vault) or ApplicationDocs.", true), (ModeKey, Str, ModeHelp, true), (PathKey, Str, "The folder, inside the folders the operator allows.", true) }.Concat(RepositoryArgs).ToArray()),
            Permissions.SourcesManage, true),
        new("update_source", "Changes a source: its name, its mode or its folder (not its kind). What is not given stays as it is.",
            McpArguments.Schema(new[] { SourceArg, (NameKey, Str, "The new name.", false), (ModeKey, Str, ModeHelp, false), (PathKey, Str, "The new folder.", false) }.Concat(RepositoryArgs).ToArray()),
            Permissions.SourcesManage, true),
        new("delete_source", "Forgets a source with its notes in Mnemarr's index. The folder and its files are not touched. The memory vault cannot be forgotten.", McpArguments.Schema(SourceArg), Permissions.SourcesManage, true),
        new("index_source", "Reads the folder of a source again now, without waiting for the next scan, so that what changed is found.", McpArguments.Schema(SourceArg), Permissions.SourcesManage, true),
        new("sync_source", "Brings the checkout of a Git source up to date now (a clone or a fast-forward, never a merge), and reads what came in.", McpArguments.Schema(SourceArg), Permissions.SourcesManage, true),
        new("request_collector_enrolment", "Asks for a machine to join Mnemarr, so that its conversations are collected. It returns a one-time code and the command to run on that machine; the code works only after a person who manages the collectors has approved the request in Mnemarr (the Machines page), and then for 15 minutes. Nothing is run on the machine by Mnemarr.",
            McpArguments.Schema(("machine", Str, "The name of the machine: letters, digits, dots, hyphens, underscores; the key it is given is bound to it.", true), ("kind", Str, "always-on for a server that runs all the time, intermittent for a workstation that sleeps (default).", false)),
            Permissions.CollectorsRequest, false),
    ];

    private readonly SourceCatalog _catalog;
    private readonly SourceIndexer _indexer;
    private readonly SourceSync _sync;
    private readonly ISystemStatusService _status;
    private readonly EmbeddingIndexer _embeddings;
    private readonly EnrolmentService _enrolments;
    private readonly InstallCommands _installs;

    public McpAdminTools(SourceCatalog catalog, SourceIndexer indexer, SourceSync sync, ISystemStatusService status, EmbeddingIndexer embeddings, EnrolmentService enrolments, InstallCommands installs)
    {
        _catalog = catalog;
        _indexer = indexer;
        _sync = sync;
        _status = status;
        _embeddings = embeddings;
        _enrolments = enrolments;
        _installs = installs;
    }

    public static IEnumerable<McpTool> Definitions => Tools;

    public static bool Handles(string name) => Tools.Any(tool => tool.Name == name);

    public async Task<object> CallAsync(string name, JsonElement arguments, McpCaller caller, CancellationToken cancellationToken) =>
        name switch
        {
            "get_status" => Status(caller),
            "create_source" => Created(Create(arguments, caller)),
            "update_source" => Created(Update(arguments, caller)),
            "delete_source" => Delete(arguments, caller),
            "index_source" => await IndexAsync(arguments, caller, cancellationToken),
            "sync_source" => await SyncAsync(arguments, caller, cancellationToken),
            "request_collector_enrolment" => RequestEnrolment(arguments, caller),
            _ => throw new RejectedException(Problem.NotFound, "There is no such tool."),
        };

    private object Status(McpCaller caller)
    {
        var status = _status.GetStatus();
        var meaning = _embeddings.Report(caller.Scope);
        return new
        {
            version = status.Version,
            uptimeSeconds = (long)status.Uptime.TotalSeconds,
            sources = _catalog.List(caller.Scope).Count,
            searchByMeaning = new { state = meaning.State.ToString().ToLowerInvariant(), model = meaning.Model, passages = meaning.Progress?.Chunks ?? 0, read = meaning.Progress?.Embedded ?? 0, reason = meaning.Reason },
        };
    }

    private Source Create(JsonElement arguments, McpCaller caller)
    {
        var kind = ParseKind(McpArguments.Text(arguments, "kind"));
        var mode = ParseMode(McpArguments.Text(arguments, ModeKey));
        return _catalog.Add(McpArguments.Text(arguments, NameKey), kind, mode, McpArguments.Text(arguments, PathKey), caller.Scope, caller.Name, Repository(arguments));
    }

    private Source Update(JsonElement arguments, McpCaller caller)
    {
        var existing = _catalog.Get(McpArguments.SourceId(_catalog, arguments, caller.Scope), caller.Scope);
        var mode = McpArguments.Optional(arguments, ModeKey) is { } named ? ParseMode(named) : existing.Mode;
        return _catalog.Update(existing.Id, McpArguments.Optional(arguments, NameKey) ?? existing.Name, mode, McpArguments.Optional(arguments, PathKey) ?? existing.Path, caller.Scope, caller.Name, Repository(arguments) ?? existing.Repository);
    }

    private object Delete(JsonElement arguments, McpCaller caller)
    {
        var id = McpArguments.SourceId(_catalog, arguments, caller.Scope);
        _catalog.Remove(id, caller.Scope, caller.Name);
        return new { removed = true };
    }

    private async Task<object> IndexAsync(JsonElement arguments, McpCaller caller, CancellationToken cancellationToken)
    {
        var report = await _indexer.IndexAsync(_catalog.Get(McpArguments.SourceId(_catalog, arguments, caller.Scope), caller.Scope), false, waitForTurn: false, cancellationToken);
        return new { added = report.Added, updated = report.Updated, removed = report.Removed, unchanged = report.Unchanged };
    }

    private async Task<object> SyncAsync(JsonElement arguments, McpCaller caller, CancellationToken cancellationToken)
    {
        var report = await _sync.SyncAsync(_catalog.Get(McpArguments.SourceId(_catalog, arguments, caller.Scope), caller.Scope), waitForTurn: false, cancellationToken);
        return new { action = report.Action.ToString(), head = report.Head };
    }

    private static object Created(Source source) => new { name = source.Name, kind = source.Kind.ToString(), mode = source.Mode.ToString(), path = source.Path, repository = source.Repository?.Url };

    private static GitRepository? Repository(JsonElement arguments) =>
        McpArguments.Optional(arguments, "repository_url") is { } url ? new GitRepository(url, McpArguments.Optional(arguments, "repository_branch"), McpArguments.Optional(arguments, "repository_credential")) : null;

    private static SourceKind ParseKind(string text) =>
        Enum.TryParse<SourceKind>(text, ignoreCase: false, out var kind) && Enum.IsDefined(kind) && kind != SourceKind.Conversations
            ? kind
            : throw new RejectedException(Problem.Invalid, "The kind of a source is Knowledge, Memory or ApplicationDocs.");

    private static SourceMode ParseMode(string text) =>
        Enum.TryParse<SourceMode>(text, ignoreCase: false, out var mode) && Enum.IsDefined(mode) ? mode : throw new RejectedException(Problem.Invalid, "The mode of a source is ReadOnly or ReadWrite.");

    private object RequestEnrolment(JsonElement arguments, McpCaller caller)
    {
        var machine = McpArguments.Text(arguments, "machine");
        var kind = MachineKind.Intermittent;
        if (McpArguments.Optional(arguments, "kind") is { } named && !MachineKinds.TryParse(named, out kind))
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
}
