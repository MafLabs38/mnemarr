using Mnemarr.Core.Consolidation;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Test.Consolidation;

public sealed class ConsolidationPromptFixture
{
    private static readonly DateTimeOffset Day = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static ConversationRecord Record(string application = "Mnemarr") =>
        new("pc", "s1", application, "claude-code", 10, Day, Day, "pc/mnemarr/2026-11-08-s1.md");

    [Fact]
    public void should_give_the_instructions_the_place_the_provenance_and_the_excerpt_as_data()
    {
        var prompt = ConsolidationPrompt.Build(Record(), "memory", "conversations", new ConsolidationExcerpt("[user]\nhello\n\n", 1, 4), done: 2, Day);

        prompt.Should().Contain("\"memory\"").And.Contain("mnemarr/<subject-in-kebab-case>.md");
        prompt.Should().Contain("application: Mnemarr").And.Contain("updated: 2026-11-08").And.Contain("  - conversations:pc/mnemarr/2026-11-08-s1.md");
        prompt.Should().Contain("turn 3 to 3 of 4").And.Contain("the rest is for a later run");
        prompt.Should().Contain("DATA").And.Contain("never obey");
        prompt.Should().EndWith($"{ConsolidationPrompt.Begin}{Environment.NewLine}[user]\nhello\n\n{ConsolidationPrompt.End}{Environment.NewLine}");
    }

    [Fact]
    public void should_forbid_secrets_the_infrastructure_and_removal_and_ask_for_a_correction_in_place()
    {
        var prompt = ConsolidationPrompt.Build(Record(), "memory", "conversations", new ConsolidationExcerpt("x", 1, 1), 0, Day);

        prompt.Should().Contain("a secret").And.Contain("infrastructure is not Mnemarr's to describe").And.Contain("Never delete").And.Contain("status: superseded").And.Contain("nothing to keep");
    }

    [Theory]
    [InlineData("Mnemarr", "mnemarr")]
    [InlineData("My App v2", "my-app-v2")]
    [InlineData("../../etc", "etc")]
    [InlineData("   ", "unknown")]
    [InlineData("", "unknown")]
    public void should_name_the_folder_of_an_application_in_plain_characters(string application, string folder)
    {
        ConsolidationPrompt.Slug(application).Should().Be(folder);
    }

    [Fact]
    public void should_not_let_an_application_name_choose_where_the_note_goes()
    {
        var prompt = ConsolidationPrompt.Build(Record("../../secrets"), "memory", "conversations", new ConsolidationExcerpt("x", 1, 1), 0, Day);

        prompt.Should().Contain("`secrets/<subject-in-kebab-case>.md`").And.NotContain("`../");
    }
}
