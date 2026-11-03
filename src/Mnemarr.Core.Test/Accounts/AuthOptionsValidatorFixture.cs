using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class AuthOptionsValidatorFixture
{
    private static string? Failure(AuthOptions options) => new AuthOptionsValidator().Validate(null, options).FailureMessage;

    [Fact]
    public void should_accept_the_defaults_a_complete_first_administrator_and_a_complete_reset()
    {
        Failure(new AuthOptions()).Should().BeNull();
        Failure(new AuthOptions { AdminUser = "root", AdminPasswordFile = "/run/secrets/admin" }).Should().BeNull();
        Failure(new AuthOptions { ResetPasswordUser = "root", ResetPasswordFile = "/run/secrets/reset" }).Should().BeNull();
    }

    [Fact]
    public void should_accept_a_plain_http_sandbox()
    {
        Failure(new AuthOptions { CookieSecurity = CookieSecurityMode.SameAsRequest }).Should().BeNull();
    }

    [Theory]
    [InlineData(0, "SESSIONHOURS")]
    [InlineData(AuthOptions.MaxSessionHours + 1, "SESSIONHOURS")]
    public void should_refuse_a_session_length_out_of_range(int hours, string setting)
    {
        Failure(new AuthOptions { SessionHours = hours }).Should().Contain(setting);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuthOptions.MaxSessionDays + 1)]
    public void should_refuse_an_absolute_lifetime_out_of_range(int days)
    {
        Failure(new AuthOptions { SessionMaxDays = days }).Should().Contain("SESSIONMAXDAYS");
    }

    [Theory]
    [InlineData("root", null)]
    [InlineData(null, "/file")]
    public void should_refuse_a_first_administrator_that_is_only_half_set(string? user, string? file)
    {
        Failure(new AuthOptions { AdminUser = user, AdminPasswordFile = file }).Should().Contain("together");
    }

    [Theory]
    [InlineData("root", null)]
    [InlineData(null, "/file")]
    public void should_refuse_a_reset_that_is_only_half_set(string? user, string? file)
    {
        Failure(new AuthOptions { ResetPasswordUser = user, ResetPasswordFile = file }).Should().Contain("RESETPASSWORD");
    }
}
