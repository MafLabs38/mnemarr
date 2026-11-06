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

    /// <summary>Changing the title of a conversation, and having a model write titles again (ADR 0028): what is said in a conversation is not changed, only how it is called.</summary>
    public const string ConversationsWrite = "conversations.write";

    /// <summary>Adding, approving, rejecting and revoking the machines that send conversations (ADR 0027): who decides which machines may.</summary>
    public const string CollectorsManage = "collectors.manage";

    /// <summary>Asking for the enrolment of a machine: harmless alone, since a person with <see cref="CollectorsManage"/> still has to approve it.</summary>
    public const string CollectorsRequest = "collectors.request";

    public static IReadOnlyList<string> All { get; } =
        [SystemRead, NotesRead, NotesWrite, Search, SourcesManage, AccountsManage, AuditRead, IngestWrite, ConversationsRead, ConversationsWrite, CollectorsManage, CollectorsRequest];
}
