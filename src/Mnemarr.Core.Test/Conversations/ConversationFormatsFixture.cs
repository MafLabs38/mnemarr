using Mnemarr.Core.Conversations;
using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Test.Conversations;

public sealed class ConversationFormatsFixture
{
    private static string Line(string type, object content, string at = "2026-11-04T14:03:00.000Z", bool meta = false, bool sidechain = false) =>
        System.Text.Json.JsonSerializer.Serialize(new { type, isMeta = meta, isSidechain = sidechain, timestamp = at, message = new { role = type, content } });

    [Fact]
    public void should_keep_what_was_said_and_leave_the_tools_the_thinking_and_the_noise()
    {
        var lines = new[]
        {
            Line("user", "Why is the build slow?\n<system-reminder>ignore me</system-reminder>"),
            Line("assistant", new object[] { new { type = "thinking", thinking = "hmm" }, new { type = "text", text = "Because of the cache." }, new { type = "tool_use", name = "Bash", input = new { command = "ls" } } }),
            Line("user", new object[] { new { type = "tool_result", content = "a very long output" } }),
            Line("user", "<command-name>/clear</command-name>"),
            Line("user", "a note to the harness", meta: true),
            Line("assistant", "said by a sub-agent", sidechain: true),
            "not json at all",
            "{\"type\":\"summary\",\"summary\":\"x\"}",
        };

        var turns = ConversationFormats.Turns(ConversationFormats.ClaudeCode, lines);

        turns.Select(turn => (turn.Role, turn.Text)).Should().Equal(("user", "Why is the build slow?"), ("assistant", "Because of the cache."));
        turns[0].At.Should().Be(new DateTimeOffset(2026, 11, 4, 14, 3, 0, TimeSpan.Zero));
    }

    [Fact]
    public void should_cut_a_turn_that_is_too_long()
    {
        var turns = ConversationFormats.Turns(ConversationFormats.ClaudeCode, [Line("assistant", new string('x', ConversationFormats.MaxTurnChars + 500))]);

        turns.Single().Text.Should().HaveLength(ConversationFormats.MaxTurnChars + "\n… [cut]".Length);
    }

    [Fact]
    public void should_read_nothing_of_a_format_it_does_not_know()
    {
        ConversationFormats.Supports("something-else").Should().BeFalse();
        ConversationFormats.Turns("something-else", [Line("user", "hello")]).Should().BeEmpty();
    }

    [Fact]
    public void should_write_a_note_whose_text_cannot_make_links_tags_or_headings_of_its_own()
    {
        var turns = new[] { new Turn("user", new DateTimeOffset(2026, 11, 4, 14, 3, 0, TimeSpan.Zero), "# Not a title\n[[not-a-link]] #not-a-tag\n```\ncode\n```") };

        var note = ConversationMarkdown.Render(ConversationFormats.ClaudeCode, "workstation", "mnemarr", "abcdef12-0000", turns[0].At!.Value, turns);

        note.Should().StartWith("---\ntags: [conversation, mnemarr]\nsource: claude-code\nmachine: workstation\napplication: mnemarr\nsession: abcdef12-0000\nstarted: 2026-11-04T14:03:00Z\n---\n# Not a title\n\n## User · 2026-11-04 14:03\n\n````text\n");
        note.Should().Contain("[[not-a-link]] #not-a-tag").And.EndWith("````\n\n");
        MarkdownNotes.Parse(note).Links.Should().BeEmpty();
        MarkdownNotes.Parse(note).Tags.Should().BeEquivalentTo(["conversation", "mnemarr"]);
        ConversationMarkdown.NotePath("workstation", "mnemarr", turns[0].At!.Value, "abcdef12-0000").Should().Be("workstation/mnemarr/2026-11-04-abcdef12-0000.md");
    }

    private static Turn User(string text) => new("user", null, text);

    // The samples of the titles are French on purpose: the people who write to the assistants do, and the heuristic (greetings, sentence ends) is
    // meant to read them. They are data of the tests, not the language of the product, which is English.
    [Theory]
    [InlineData("Peux-tu corriger le calcul des fins de ligne ? Merci.", "Peux-tu corriger le calcul des fins de ligne ?")]
    [InlineData("ok", "ok")]
    [InlineData("[[lien]] et #étiquette : que faire ?", "lien et étiquette : que faire ?")]
    [InlineData("# Plan de la migration\nOn migre la base.", "Plan de la migration")]
    public void should_title_a_conversation_from_the_first_sentence_that_says_something(string first, string title) =>
        ConversationMarkdown.Title([User(first)], "abcdef12-0000").Should().Be(title);

    [Fact]
    public void should_skip_a_greeting_a_code_block_and_an_address_to_find_what_the_conversation_is_about()
    {
        var turns = new[] { User("Salut !"), User("```\nerror: boom\n```\nhttps://example.com/log"), User("Pourquoi le build échoue-t-il sur la CI ?") };

        ConversationMarkdown.Title(turns, "abcdef12-0000").Should().Be("Pourquoi le build échoue-t-il sur la CI ?");
    }

    [Fact]
    public void should_cut_a_long_title()
    {
        var title = ConversationMarkdown.Title([User(new string('a', 200))], "abcdef12-0000");

        title.Should().HaveLength(81).And.EndWith("…");
    }

    [Fact]
    public void should_mask_what_a_line_says_once_its_escapes_are_decoded()
    {
        // The secret is written with a JSON escape for its first letter: the line, as it is, shows no token.
        var line = "{\"type\":\"user\",\"message\":{\"content\":\"here: \\u0067hp_" + new string('a', 36) + "\"}}";

        var turns = ConversationFormats.Turns(ConversationFormats.ClaudeCode, [line]);

        turns.Single().Text.Should().Be("here: [masked:github-token]");
    }

    [Fact]
    public void should_make_the_same_path_and_the_same_headings_whatever_the_culture_of_the_host()
    {
        var thai = System.Globalization.CultureInfo.GetCultureInfo("th-TH");
        Assert.SkipUnless(new DateTime(2026, 11, 4).ToString("yyyy", thai) != "2026", "this runtime has no culture data (invariant globalization): the Thai calendar cannot be tried");
        var at = new DateTimeOffset(2026, 11, 4, 14, 3, 0, TimeSpan.Zero);
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = thai;

            ConversationMarkdown.NotePath("workstation", "mnemarr", at, "s1").Should().Be("workstation/mnemarr/2026-11-04-s1.md");
            ConversationMarkdown.Sections([new Turn("user", at, "hello")]).Should().StartWith("## User · 2026-11-04 14:03");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void should_fence_a_turn_that_holds_a_long_run_of_backticks_with_a_longer_fence()
    {
        var note = ConversationMarkdown.Sections([User("before `````\n[[not-a-link]] #not-a-tag\n````` after")]);

        note.Should().Contain("``````text\n").And.EndWith("``````\n\n");
        MarkdownNotes.Parse(note).Links.Should().BeEmpty();
        MarkdownNotes.Parse(note).Tags.Should().BeEmpty();
    }

    [Fact]
    public void should_read_a_line_with_no_date_or_a_bad_one_and_join_the_blocks_of_text_of_a_turn()
    {
        var lines = new[]
        {
            Line("user", "no date here", at: string.Empty),
            Line("user", "a bad date", at: "yesterday-ish"),
            Line("assistant", new object[] { new { type = "text", text = "first block" }, new { type = "text", text = "second block" } }),
            Line("user", string.Empty),
        };

        var turns = ConversationFormats.Turns(ConversationFormats.ClaudeCode, lines);

        turns.Select(turn => turn.At).Should().OnlyContain(at => at == null || at.Value.Year == 2026);
        turns.Take(2).Select(turn => turn.At).Should().OnlyContain(at => at == null);
        turns.Select(turn => turn.Text).Should().Equal("no date here", "a bad date", "first block\n\nsecond block");
    }
}
