using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AuthOptionsValidatorFixture
{
    private static string? Failure(AuthOptions options) => new AuthOptionsValidator().Validate(null, options).FailureMessage;

    [Fact]
    public void should_accept_the_defaults_and_a_complete_first_administrator()
    {
        Failure(new AuthOptions()).Should().BeNull();
        Failure(new AuthOptions { AdminUser = "root", AdminPasswordFile = "/run/secrets/admin" }).Should().BeNull();
    }

    [Theory]
    [InlineData("Never")]
    [InlineData("always")]
    [InlineData("")]
    public void should_refuse_a_cookie_security_that_is_not_a_known_one(string value)
    {
        Failure(new AuthOptions { CookieSecurity = value }).Should().Contain("COOKIESECURITY");
    }

    [Fact]
    public void should_accept_a_plain_http_sandbox()
    {
        Failure(new AuthOptions { CookieSecurity = "SameAsRequest" }).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(721)]
    public void should_refuse_a_session_length_out_of_range(int hours)
    {
        Failure(new AuthOptions { SessionHours = hours }).Should().Contain("SESSIONHOURS");
    }

    [Theory]
    [InlineData("root", null)]
    [InlineData(null, "/file")]
    public void should_refuse_a_first_administrator_that_is_only_half_set(string? user, string? file)
    {
        Failure(new AuthOptions { AdminUser = user, AdminPasswordFile = file }).Should().Contain("together");
    }
}
