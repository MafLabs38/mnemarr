namespace Mnemarr.Core.Accounts;

/// <summary>A named set of permissions. The built-in roles are in code, not stored.</summary>
public sealed record Role(string Name, string Description, IReadOnlySet<string> Permissions, bool ForUsers, bool ForServices);

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Reader = "Reader";
    public const string Assistant = "Assistant";
    public const string Collector = "Collector";
    public const string Monitor = "Monitor";

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
    ];

    public static Role? Find(string name) => All.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.Ordinal));

    private static HashSet<string> Set(params string[] permissions) => new(permissions, StringComparer.Ordinal);
}
