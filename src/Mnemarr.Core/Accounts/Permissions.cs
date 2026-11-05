namespace Mnemarr.Core.Accounts;

/// <summary>
/// The permissions, a closed list: one authorization policy exists for each, and a role is a set of them (ADR 0016).
/// A new permission is granted to the right roles by the commit that adds it.
/// </summary>
public static class Permissions
{
    public const string SystemRead = "system.read";
    public const string NotesRead = "notes.read";
    public const string NotesWrite = "notes.write";
    public const string Search = "search";
    public const string SourcesManage = "sources.manage";
    public const string AccountsManage = "accounts.manage";
    public const string AuditRead = "audit.read";
    public const string IngestWrite = "ingest.write";

    /// <summary>Reading the conversations of the assistants: what people said to them, which is more private than any note.</summary>
    public const string ConversationsRead = "conversations.read";

    public static IReadOnlyList<string> All { get; } =
        [SystemRead, NotesRead, NotesWrite, Search, SourcesManage, AccountsManage, AuditRead, IngestWrite, ConversationsRead];
}
