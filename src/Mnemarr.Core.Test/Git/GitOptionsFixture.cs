using Microsoft.Extensions.Options;
using Mnemarr.Core.Git;

namespace Mnemarr.Core.Test.Git;

public sealed class GitOptionsFixture
{
    private static ValidateOptionsResult Validate(GitOptions options) => new GitOptionsValidator().Validate(null, options);

    [Fact]
    public void should_accept_the_defaults()
    {
        Validate(new GitOptions()).Succeeded.Should().BeTrue();
        new GitOptions().SyncIntervalSeconds.Should().Be(300);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(86_401)]
    public void should_refuse_an_interval_out_of_range_and_name_the_setting(int seconds)
    {
        var result = Validate(new GitOptions { SyncIntervalSeconds = seconds });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("MNEMARR__GIT__SYNCINTERVALSECONDS");
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://github.example")]
    [InlineData("github.example/org")]
    [InlineData("github.example:443")]
    public void should_refuse_a_host_that_is_not_a_bare_host_name(string host)
    {
        Validate(new GitOptions { Hosts = [host] }).Failed.Should().BeTrue();
    }

    [Fact]
    public void should_accept_hosts_and_credentials_with_a_token_or_a_token_file()
    {
        var options = new GitOptions
        {
            Hosts = ["github.example"],
            Credentials =
            {
                ["a"] = new GitCredentialOptions { Host = "github.example", Token = "t" },
                ["b"] = new GitCredentialOptions { Host = "git.example", TokenFile = "/run/secrets/token" },
            },
        };

        Validate(options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "t", null)]
    [InlineData("github.example/x", "t", null)]
    [InlineData("github.example", null, null)]
    [InlineData("github.example", "t", "/run/secrets/token")]
    [InlineData("github.example", null, "relative/token")]
    public void should_refuse_a_credential_that_has_no_host_no_secret_two_secrets_or_a_relative_file_and_never_print_a_token(string host, string? token, string? file)
    {
        var result = Validate(new GitOptions { Credentials = { ["a"] = new GitCredentialOptions { Host = host, Token = token, TokenFile = file } } });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Git:Credentials:a");
        if (token is not null)
        {
            result.FailureMessage.Should().NotContain(token + "\n");
        }
    }
}

public sealed class GitCredentialsFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static GitCredentials Credentials(GitOptions options) => new(Options.Create(options));

    [Fact]
    public void should_give_the_host_in_lower_case_and_the_token_of_a_credential()
    {
        var credentials = Credentials(new GitOptions { Credentials = { ["github"] = new GitCredentialOptions { Host = " GitHub.example ", Token = " tok ", Username = "me" } } });

        credentials.HostOf("GITHUB").Should().Be("github.example");
        credentials.Secret("github").Should().Be(new GitSecret("me", "tok"));
    }

    [Fact]
    public void should_know_nothing_of_a_credential_that_is_not_declared()
    {
        var credentials = Credentials(new GitOptions());

        credentials.HostOf("nope").Should().BeNull();
        credentials.Secret("nope").Should().BeNull();
    }

    [Fact]
    public void should_read_the_token_file_each_time_so_that_a_rotated_token_is_used_without_a_restart()
    {
        var file = Path.Combine(_workspace.Root, "token");
        File.WriteAllText(file, "first\n");
        var credentials = Credentials(new GitOptions { Credentials = { ["github"] = new GitCredentialOptions { Host = "github.example", TokenFile = file } } });

        var first = credentials.Secret("github");
        File.WriteAllText(file, "second\n");
        var second = credentials.Secret("github");

        first!.Token.Should().Be("first");
        second!.Token.Should().Be("second");
    }

    [Fact]
    public void should_say_nothing_of_a_token_file_that_cannot_be_read_or_is_empty()
    {
        var empty = Path.Combine(_workspace.Root, "empty");
        File.WriteAllText(empty, " \n");
        var credentials = Credentials(new GitOptions
        {
            Credentials =
            {
                ["missing"] = new GitCredentialOptions { Host = "github.example", TokenFile = Path.Combine(_workspace.Root, "absent") },
                ["empty"] = new GitCredentialOptions { Host = "github.example", TokenFile = empty },
            },
        });

        credentials.Secret("missing").Should().BeNull();
        credentials.Secret("empty").Should().BeNull();
    }
}
