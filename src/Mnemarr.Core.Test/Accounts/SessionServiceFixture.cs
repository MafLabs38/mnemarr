using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

public sealed class SessionServiceFixture : IDisposable
{
    private readonly AccountsHarness _h = new(new AuthOptions { SessionMaxDays = 30 });

    public void Dispose() => _h.Dispose();

    [Fact]
    public void should_know_the_account_of_a_session_that_was_started_and_give_each_session_its_own_id()
    {
        var alice = _h.Reader();

        var first = _h.Sessions.Start(alice.Id);
        var second = _h.Sessions.Start(alice.Id);

        first.Should().NotBe(second).And.HaveLength(64);
        _h.Sessions.AccountOf(first).Should().Be(alice.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    public void should_know_nothing_of_a_session_that_was_never_started(string id)
    {
        _h.Sessions.AccountOf(id).Should().BeNull();
    }

    [Fact]
    public void should_end_a_session_when_asked_and_leave_the_others()
    {
        var alice = _h.Reader();
        var one = _h.Sessions.Start(alice.Id);
        var two = _h.Sessions.Start(alice.Id);

        _h.Sessions.End(one);

        _h.Sessions.AccountOf(one).Should().BeNull();
        _h.Sessions.AccountOf(two).Should().Be(alice.Id);
    }

    [Fact]
    public void should_end_every_session_of_an_account_except_the_one_kept_and_not_those_of_another_account()
    {
        var alice = _h.Reader("alice");
        var bob = _h.Reader("bob");
        var kept = _h.Sessions.Start(alice.Id);
        var gone = _h.Sessions.Start(alice.Id);
        var other = _h.Sessions.Start(bob.Id);

        _h.Sessions.EndAll(alice.Id, except: kept);

        _h.Sessions.AccountOf(kept).Should().Be(alice.Id);
        _h.Sessions.AccountOf(gone).Should().BeNull();
        _h.Sessions.AccountOf(other).Should().Be(bob.Id);
    }

    [Fact]
    public void should_end_a_session_at_the_end_of_its_absolute_lifetime_however_much_it_was_used()
    {
        var alice = _h.Reader();
        var session = _h.Sessions.Start(alice.Id);

        _h.Clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        _h.Sessions.AccountOf(session).Should().Be(alice.Id);
        _h.Clock.Advance(TimeSpan.FromSeconds(1));

        _h.Sessions.AccountOf(session).Should().BeNull();
    }

    [Fact]
    public void should_forget_the_sessions_that_expired_when_a_new_one_starts()
    {
        var alice = _h.Reader();
        _h.Sessions.Start(alice.Id);
        _h.Clock.Advance(TimeSpan.FromDays(31));

        _h.Sessions.Start(alice.Id);

        using var connection = _h.Workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions";
        command.ExecuteScalar().Should().Be(1L);
    }

    [Fact]
    public void should_remove_the_sessions_with_their_account()
    {
        _h.Admin();
        var alice = _h.Reader();
        var session = _h.Sessions.Start(alice.Id);

        _h.Service.Delete(alice.Id, "root");

        _h.Sessions.AccountOf(session).Should().BeNull();
    }

    [Fact]
    public void should_keep_only_a_hash_of_the_session_id_so_that_a_copy_of_the_database_is_not_a_cookie()
    {
        var alice = _h.Reader();

        var id = _h.Sessions.Start(alice.Id);

        _h.SessionStore.AccountOf(id, _h.Clock.GetUtcNow()).Should().BeNull();
        _h.SessionStore.AccountOf(SessionService.Hash(id), _h.Clock.GetUtcNow()).Should().Be(alice.Id);
    }
}
