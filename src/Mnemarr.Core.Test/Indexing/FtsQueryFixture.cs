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
        FtsQuery.From("name:\"x\" NEAR(a b) -c* OR 1=1").Should().Be("\"name\" OR \"x\" OR \"NEAR\" OR \"a\" OR \"b\" OR \"c\" OR \"OR\" OR \"1\"");
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
