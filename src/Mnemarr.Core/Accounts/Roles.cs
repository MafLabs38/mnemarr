namespace Mnemarr.Core.Accounts;

/// <summary>A named set of permissions. The built-in roles are in code, not stored.</summary>
/// <param name="Name">The name of the role.</param>
/// <param name="Description">What it is for.</param>
/// <param name="Permissions">What it allows.</param>
/// <param name="ForUsers">Whether a person can hold it.</param>
/// <param name="ForServices">Whether a service account can hold it.</param>
/// <param name="System">A role of Mnemarr itself (the account of the consolidation): no person gives it to an account, and it is not listed.</param>
public sealed record Role(string Name, string Description, IReadOnlySet<string> Permissions, bool ForUsers, bool ForServices, bool System = false);

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Reader = "Reader";
    public const string Assistant = "Assistant";
    public const string Collector = "Collector";
    public const string Monitor = "Monitor";
    public const string Consolidator = "Consolidator";

    public static IReadOnlyList<Role> All { get; } =
    [
        new(Admin, "Everything, accounts included.", Permissions.All.ToHashSet(StringComparer.Ordinal), ForUsers: true, ForServices: false),
        new(
            Operator,
            "Reads and writes notes, searches, manages sources, reads the status of the system.",
            Set(Permissions.NotesRead, Permissions.NotesWrite, Permissions.Search, Permissions.SourcesManage, Permissions.SystemRead, Permissions.ChatUse),
            ForUsers: true,
            ForServices: true),
        new(Reader, "Reads notes and searches, and may ask the chat about them.", Set(Permissions.NotesRead, Permissions.Search, Permissions.ChatUse), ForUsers: true, ForServices: true),
        new(Assistant, "An assistant: reads and writes notes, searches, reads the conversations, can ask for a machine to be enrolled (a key is given to an assistant on purpose, and can be limited to sources).", Set(Permissions.NotesRead, Permissions.NotesWrite, Permissions.Search, Permissions.ConversationsRead, Permissions.CollectorsRequest), ForUsers: false, ForServices: true),
        new(Collector, "A collector: sends conversations, nothing else.", Set(Permissions.IngestWrite), ForUsers: false, ForServices: true),
        new(Monitor, "A companion that watches: reads the status of the system, nothing else.", Set(Permissions.SystemRead), ForUsers: false, ForServices: true),
        new(
            Consolidator,
            "The consolidation (ADR 0036): reads notes and writes notes in the memory vault, and reads no conversation (it is given the excerpt it works on); the server holds it to the memory vault.",
            Set(Permissions.NotesRead, Permissions.NotesWrite, Permissions.Search),
            ForUsers: false,
            ForServices: false,
            System: true),
    ];

    public static Role? Find(string name) => All.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.Ordinal));

    private static HashSet<string> Set(params string[] permissions) => new(permissions, StringComparer.Ordinal);
}
