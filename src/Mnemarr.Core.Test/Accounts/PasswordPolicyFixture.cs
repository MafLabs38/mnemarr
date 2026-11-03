using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class PasswordPolicyFixture
{
    [Theory]
    [InlineData(PasswordPolicy.MinLength)]
    [InlineData(PasswordPolicy.MaxLength)]
    public void should_accept_a_password_at_the_limits(int length)
    {
        PasswordPolicy.Problem(new string('x', length), "alice").Should().BeNull();
    }

    [Theory]
    [InlineData(PasswordPolicy.MinLength - 1, "at least")]
    [InlineData(PasswordPolicy.MaxLength + 1, "at most")]
    public void should_refuse_a_password_outside_the_limits(int length, string reason)
    {
        PasswordPolicy.Problem(new string('x', length), "alice").Should().Contain(reason);
    }

    [Fact]
    public void should_refuse_the_name_of_the_account_whatever_its_case()
    {
        PasswordPolicy.Problem("ALICE-THE-USER", "alice-the-user").Should().Contain("name of the account");
    }
}
