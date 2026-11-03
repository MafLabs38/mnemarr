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

    public static IReadOnlyList<Role> All { get; } =
    [
        new(Admin, "Everything, accounts included.", Permissions.All.ToHashSet(StringComparer.Ordinal), ForUsers: true, ForServices: false),
        new(
            Operator,
            "Reads notes, searches, manages sources, reads the status of the system. Notes are written by assistants, not by people.",
            Set(Permissions.NotesRead, Permissions.Search, Permissions.SourcesManage, Permissions.SystemRead),
            ForUsers: true,
            ForServices: true),
        new(Reader, "Reads notes and searches, nothing else.", Set(Permissions.NotesRead, Permissions.Search), ForUsers: true, ForServices: true),
        new(Assistant, "An assistant: reads and writes notes, searches.", Set(Permissions.NotesRead, Permissions.NotesWrite, Permissions.Search), ForUsers: false, ForServices: true),
        new(Collector, "A collector: sends conversations, nothing else.", Set(Permissions.IngestWrite), ForUsers: false, ForServices: true),
    ];

    public static Role? Find(string name) => All.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.Ordinal));

    private static HashSet<string> Set(params string[] permissions) => new(permissions, StringComparer.Ordinal);
}
