using Microsoft.Extensions.Options;
using Mnemarr.Core.Git;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Git;

public sealed class GitPolicyFixture
{
    private static GitPolicy Policy(GitOptions? options = null)
    {
        var wrapped = Options.Create(options ?? new GitOptions
        {
            Credentials = { ["github"] = new GitCredentialOptions { Host = "github.example", Token = "secret-token" } },
        });
        return new GitPolicy(wrapped, new GitCredentials(wrapped));
    }

    [Fact]
    public void should_give_the_repository_in_its_normal_form()
    {
        var repository = Policy().Check(new GitRepository(" https://GitHub.example/org/notes.git/ ", " main ", " github "));

        repository.Should().Be(new GitRepository("https://github.example/org/notes.git", "main", "github"));
    }

    [Fact]
    public void should_accept_a_repository_with_no_branch_and_no_credential()
    {
        Policy().Check(new GitRepository("https://github.example/org/notes", null, "  ")).Should().Be(new GitRepository("https://github.example/org/notes", null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("http://github.example/org/notes")]
    [InlineData("ssh://git@github.example/org/notes")]
    [InlineData("git@github.example:org/notes.git")]
    [InlineData("file:///srv/notes")]
    [InlineData("/srv/notes")]
    [InlineData("https://github.example")]
    [InlineData("https://github.example/")]
    public void should_refuse_an_address_that_is_not_https_with_a_host_and_a_path(string url)
    {
        var refusal = Assert.Throws<RejectedException>(() => Policy().Check(new GitRepository(url)));

        refusal.Problem.Should().Be(Problem.Invalid);
    }

    [Theory]
    [InlineData("https://user:pass@github.example/org/notes")]
    [InlineData("https://token@github.example/org/notes")]
    [InlineData("https://github.example/org/notes?token=abc")]
    [InlineData("https://github.example/org/notes#frag")]
    public void should_refuse_an_address_that_carries_a_secret_or_more_than_an_address(string url)
    {
        var refusal = Assert.Throws<RejectedException>(() => Policy().Check(new GitRepository(url)));

        refusal.Message.Should().Contain("never written in the address").And.NotContain(url).And.NotContain("token=abc");
    }

    [Fact]
    public void should_refuse_an_address_that_is_too_long()
    {
        Assert.Throws<RejectedException>(() => Policy().Check(new GitRepository($"https://github.example/{new string('a', 2100)}")));
    }

    [Fact]
    public void should_refuse_a_host_the_operator_does_not_allow_and_accept_one_it_does()
    {
        var policy = Policy(new GitOptions { Hosts = ["github.example"] });

        Assert.Throws<RejectedException>(() => policy.Check(new GitRepository("https://elsewhere.example/org/notes"))).Message.Should().Contain("does not allow");
        policy.Check(new GitRepository("https://GITHUB.example/org/notes")).Url.Should().Be("https://github.example/org/notes");
    }

    [Fact]
    public void should_say_the_same_for_a_credential_that_does_not_exist_and_for_one_that_is_for_another_host()
    {
        var policy = Policy();

        var missing = Assert.Throws<RejectedException>(() => policy.Check(new GitRepository("https://github.example/org/notes", null, "nope")));
        var other = Assert.Throws<RejectedException>(() => policy.Check(new GitRepository("https://elsewhere.example/org/notes", null, "github")));

        other.Message.Should().Be(missing.Message);
        other.Message.Should().NotContain("github.example").And.NotContain("secret-token");
    }

    [Theory]
    [InlineData("-delete")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("main.")]
    [InlineData("topic.lock")]
    [InlineData("a..b")]
    [InlineData("a@{b")]
    [InlineData("a b")]
    [InlineData("a~b")]
    [InlineData("a^b")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a[b")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void should_refuse_a_branch_name_git_does_not_accept_or_that_could_be_read_as_something_else(string branch)
    {
        Assert.Throws<RejectedException>(() => Policy().Check(new GitRepository("https://github.example/org/notes", branch)));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.2")]
    [InlineData("feature/notes-ü")]
    public void should_accept_an_ordinary_branch_name(string branch)
    {
        Policy().Check(new GitRepository("https://github.example/org/notes", branch)).Branch.Should().Be(branch);
    }
}
