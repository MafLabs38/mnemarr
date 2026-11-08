using Mnemarr.Core.Chat;

namespace Mnemarr.Core.Test.Chat;

public sealed class AgentOptionsFixture
{
    private static readonly string Secret = new('s', AgentOptions.MinimumSecretLength);

    [Fact]
    public void should_be_off_without_an_address_whatever_else_is_set()
    {
        new AgentOptions { Secret = "short" }.Enabled.Should().BeFalse();
        new AgentOptionsValidator().Validate(null, new AgentOptions { Secret = "short" }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://mnemarr-agent:8489", true)]
    [InlineData("https://agent.example", true)]
    [InlineData("ftp://agent.example", false)]
    [InlineData("not an address", false)]
    [InlineData("http://user:password@agent.example", false)]
    public void should_check_the_address_of_the_runner(string url, bool valid)
    {
        new AgentOptionsValidator().Validate(null, new AgentOptions { Url = url, Secret = Secret }).Succeeded.Should().Be(valid);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   short   ", false)]
    [InlineData("0123456789012345678901234567890", false)]
    [InlineData("01234567890123456789012345678901", true)]
    public void should_ask_for_a_long_secret_when_the_chat_is_on(string? secret, bool valid)
    {
        new AgentOptionsValidator().Validate(null, new AgentOptions { Url = "http://agent:8489", Secret = secret }).Succeeded.Should().Be(valid);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void should_keep_the_conversations_of_a_person_between_one_and_a_thousand(int cap, bool valid)
    {
        new AgentOptionsValidator().Validate(null, new AgentOptions { Url = "http://agent:8489", Secret = Secret, MaxConversationsPerPerson = cap }).Succeeded.Should().Be(valid);
    }
}
