using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Test.Indexing;

public sealed class FtsQueryFixture
{
    [Fact]
    public void should_quote_every_word_and_accept_any_of_them()
    {
        FtsQuery.From("kubernetes ingress").Should().Be("\"kubernetes\" OR \"ingress\"");
    }

    [Theory]
    [InlineData("comment sauvegarder la base de données ?", "\"sauvegarder\" OR \"base\" OR \"données\"")]
    [InlineData("how do I back up the database", "\"back\" OR \"up\" OR \"database\"")]
    [InlineData("what is it", "\"what\" OR \"is\" OR \"it\"")]
    [InlineData("La SAUVEGARDE DE Nuit", "\"SAUVEGARDE\" OR \"Nuit\"")]
    public void should_leave_out_the_small_words_of_a_question_unless_it_is_made_only_of_them(string question, string expected)
    {
        FtsQuery.From(question).Should().Be(expected);
    }

    [Theory]
    [InlineData("le la de", 0)]
    [InlineData("backup nuit", 2)]
    [InlineData("comment sauvegarder la base de données ?", 3)]
    [InlineData("quelle protection contre la perte de mes données dimanche", 5)]
    public void should_count_the_words_that_are_not_small_ones(string question, int expected)
    {
        FtsQuery.Meaningful(question).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"*()-:^")]
    public void should_give_nothing_when_there_is_no_word(string text)
    {
        FtsQuery.From(text).Should().BeNull();
    }

    [Fact]
    public void should_never_let_the_syntax_of_the_full_text_engine_through()
    {
        // The operators and the small words are plain words, and the small ones (a, OR) are left out.
        FtsQuery.From("name:\"x\" NEAR(a b) -c* OR 1=1").Should().Be("\"name\" OR \"x\" OR \"NEAR\" OR \"b\" OR \"c\" OR \"1\"");
    }

    [Fact]
    public void should_keep_letters_with_accents_and_digits_and_drop_repeats()
    {
        FtsQuery.From("réseau réseau k8s").Should().Be("\"réseau\" OR \"k8s\"");
    }

    [Fact]
    public void should_look_at_the_first_sixteen_words_only()
    {
        var query = FtsQuery.From(string.Join(' ', Enumerable.Range(0, 40).Select(i => $"w{i}")));

        query!.Split(" OR ").Should().HaveCount(16);
    }
}
