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

    /// <summary>
    /// Adding, approving, rejecting and revoking the machines that send conversations (ADR 0027): who decides which machines may. Also renaming, merging and removing
    /// them, and <b>deleting a machine with everything it sent</b>, which cannot be undone (ADR 0031): it is the permission of an administrator.
    /// </summary>
    public const string CollectorsManage = "collectors.manage";

    /// <summary>Asking for the enrolment of a machine: harmless alone, since a person with <see cref="CollectorsManage"/> still has to approve it.</summary>
    public const string CollectorsRequest = "collectors.request";

    /// <summary>
    /// Using the chat (ADR 0035): talking to a model that uses Mnemarr's tools as the person does. It gives nothing by itself: what the model may do is what the person's role
    /// allows, since it holds a token of that person, and every change waits for their approval.
    /// </summary>
    public const string ChatUse = "chat.use";

    public static IReadOnlyList<string> All { get; } =
        [SystemRead, NotesRead, NotesWrite, Search, SourcesManage, AccountsManage, AuditRead, IngestWrite, ConversationsRead, ConversationsWrite, CollectorsManage, CollectorsRequest, ChatUse];
}
