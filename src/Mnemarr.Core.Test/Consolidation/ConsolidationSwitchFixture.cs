using Mnemarr.Core.Collectors;
using Mnemarr.Core.Consolidation;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Test.Consolidation;

/// <summary>
/// Whether the conversations of a machine are turned into notes (ADR 0041): what the machine says, else the default of the format. A conversation of a machine that does not consolidate is never
/// picked, never counted as waiting; turning it on makes the conversations that were left eligible, from their start.
/// </summary>
public sealed class ConsolidationSwitchFixture : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly TestWorkspace _workspace = new();
    private readonly SqliteConsolidationStore _store;
    private readonly SqliteConversationStore _conversations;
    private readonly SqliteMachineStore _machines;

    public ConsolidationSwitchFixture()
    {
        _store = new SqliteConsolidationStore(_workspace.Database);
        _conversations = new SqliteConversationStore(_workspace.Database);
        _machines = new SqliteMachineStore(_workspace.Database);
    }

    public void Dispose() => _workspace.Dispose();

    private void Machine(string name) => _machines.Enrol(new Machine(name, MachineKind.AlwaysOn, Guid.NewGuid(), Now.AddDays(-1), null, null, 0, null, null));

    private void Conversation(string machine, string format, string session = "s") =>
        _conversations.Save(new ConversationRecord(machine, session, "app", format, 40, Now.AddHours(-3), Now.AddHours(-2), $"{machine}/app/{session}.md"));

    private IReadOnlyList<string> Pending() => [.. _store.Pending(Now.AddMinutes(-30), maxTries: 3, limit: 50).Select(item => $"{item.Machine}/{item.Session}")];

    [Fact]
    public void should_consolidate_the_conversations_of_a_command_line_assistant_and_not_the_ones_of_a_satellite_by_default()
    {
        Machine("pc");
        Machine("gemini");
        Conversation("pc", ConversationFormats.ClaudeCode);
        Conversation("gemini", ConversationFormats.ChatTurns);

        Pending().Should().Equal("pc/s");
        _store.Counts(Now.Date, 3).Waiting.Should().Be(1, "a conversation that is never consolidated is not waiting");
    }

    [Fact]
    public void should_follow_what_the_machine_says_whatever_the_format()
    {
        Machine("pc");
        Machine("gemini");
        Conversation("pc", ConversationFormats.ClaudeCode);
        Conversation("gemini", ConversationFormats.ChatTurns);
        _machines.SetConsolidate("pc", false);
        _machines.SetConsolidate("gemini", true);

        Pending().Should().Equal("gemini/s");
        _store.Counts(Now.Date, 3).Waiting.Should().Be(1);
    }

    [Fact]
    public void should_make_a_conversation_eligible_when_it_is_switched_on_later_and_not_when_it_is_switched_off_again()
    {
        Machine("gemini");
        Conversation("gemini", ConversationFormats.ChatTurns);
        Pending().Should().BeEmpty();

        _machines.SetConsolidate("gemini", true);
        Pending().Should().Equal("gemini/s");

        _machines.SetConsolidate("gemini", null);
        Pending().Should().BeEmpty("back to the default of the format");
    }

    [Fact]
    public void should_take_the_default_of_the_format_for_a_conversation_whose_machine_never_joined()
    {
        Conversation("unknown-pc", ConversationFormats.ClaudeCode, "a");
        Conversation("unknown-web", ConversationFormats.ChatTurns, "b");

        Pending().Should().Equal("unknown-pc/a");
        _store.Counts(Now.Date, 3).Waiting.Should().Be(1);
    }

    [Fact]
    public void should_say_the_effective_value_for_the_machines_page()
    {
        ConsolidationDefaults.For(null, [ConversationFormats.ClaudeCode]).Should().BeTrue();
        ConsolidationDefaults.For(null, [ConversationFormats.ChatTurns]).Should().BeFalse();
        ConsolidationDefaults.For(null, [ConversationFormats.ChatTurns, ConversationFormats.ClaudeCode]).Should().BeTrue();
        ConsolidationDefaults.For(null, null).Should().BeTrue("a machine that sent nothing yet is taken for the default");
        ConsolidationDefaults.For(false, [ConversationFormats.ClaudeCode]).Should().BeFalse();
        ConsolidationDefaults.For(true, [ConversationFormats.ChatTurns]).Should().BeTrue();
    }
}
