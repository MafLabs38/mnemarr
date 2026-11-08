using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Consolidation;

/// <summary>What the consolidation reads of a conversation: the record Mnemarr holds, and the turns that the lines it kept say.</summary>
public sealed class ConsolidationInput
{
    private readonly IConversationStore _conversations;
    private readonly RawConversations _raw;

    public ConsolidationInput(IConversationStore conversations, RawConversations raw)
    {
        _conversations = conversations;
        _raw = raw;
    }

    public ConversationRecord? Find(string machine, string session) => _conversations.Find(machine, session);

    /// <summary>The turns of the conversation, in order; none for a format that has no reader.</summary>
    public IReadOnlyList<Turn> Turns(ConversationRecord record) =>
        ConversationFormats.Supports(record.Format) ? ConversationFormats.Turns(record.Format, _raw.Read(record.Machine, record.Session, record.Lines)) : [];
}
