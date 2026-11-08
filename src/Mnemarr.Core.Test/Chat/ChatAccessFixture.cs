using Mnemarr.Core.Accounts;
using Mnemarr.Core.Chat;
using Mnemarr.Core.Test.Accounts;

namespace Mnemarr.Core.Test.Chat;

/// <summary>The rules of the chat that Mnemarr keeps (ADR 0035): whose a conversation is, and what the token of the model may do and for how long.</summary>
public sealed class ChatAccessFixture : IDisposable
{
    private readonly AccountsHarness _h = new();
    private readonly ChatAccess _subject;

    public ChatAccessFixture()
    {
        _subject = new ChatAccess(_h.Workspace.Database, _h.Accounts, _h.Clock);
    }

    public void Dispose() => _h.Dispose();

    private static string NewId() => Guid.NewGuid().ToString();

    [Fact]
    public void should_keep_a_conversation_for_its_owner_and_for_nobody_else()
    {
        var alice = _h.Reader("alice");
        var bob = _h.Reader("bob");
        var id = NewId();

        _subject.Register(id, alice);

        _subject.CanAccess(id, alice.Id).Should().BeTrue();
        _subject.CanAccess(id, bob.Id).Should().BeFalse();
        _subject.OwnedBy(alice.Id).Should().Equal(id);
        _subject.OwnedBy(bob.Id).Should().BeEmpty();
    }

    [Fact]
    public void should_forget_a_conversation_and_its_tokens_together()
    {
        var alice = _h.Reader("alice");
        var id = NewId();
        _subject.Register(id, alice);
        var token = _subject.Mint(alice.Id, id);

        _subject.Forget(id);

        _subject.CanAccess(id, alice.Id).Should().BeFalse();
        _subject.Authenticate(token).Should().BeNull("a conversation that was deleted takes the hands of its model with it");
    }

    [Fact]
    public void should_forget_the_conversations_the_runner_no_longer_lists_and_keep_the_others()
    {
        var alice = _h.Reader("alice");
        var kept = NewId();
        var gone = NewId();
        _subject.Register(kept, alice);
        _subject.Register(gone, alice);
        var token = _subject.Mint(alice.Id, gone);
        _h.Clock.Advance(ChatAccess.NewConversationGrace + TimeSpan.FromSeconds(1));

        _subject.Keep(new HashSet<string> { kept });

        _subject.OwnedBy(alice.Id).Should().Equal(kept);
        _subject.Authenticate(token).Should().BeNull();
    }

    [Fact]
    public void should_not_forget_a_conversation_that_was_made_after_the_list_was_asked_for()
    {
        var alice = _h.Reader("alice");
        var id = NewId();
        _subject.Register(id, alice);

        _subject.Keep(new HashSet<string> { NewId() });

        _subject.CanAccess(id, alice.Id).Should().BeTrue("the runner's list was asked for before it existed");
    }

    [Fact]
    public void should_keep_every_owner_when_the_runner_lists_nothing_at_all()
    {
        var alice = _h.Reader("alice");
        var id = NewId();
        _subject.Register(id, alice);
        _h.Clock.Advance(ChatAccess.NewConversationGrace + TimeSpan.FromMinutes(1));

        _subject.Keep(new HashSet<string>());

        _subject.CanAccess(id, alice.Id).Should().BeTrue("a runner started on an empty or unmounted volume has not lost every conversation for good");
    }

    [Fact]
    public void should_take_a_token_back()
    {
        var alice = _h.Reader("alice");
        var token = _subject.Mint(alice.Id, NewId());

        _subject.Revoke(token);

        _subject.Authenticate(token).Should().BeNull();
    }

    [Fact]
    public void should_never_keep_the_token_itself_but_only_its_hash()
    {
        var alice = _h.Reader("alice");
        var id = NewId();
        _subject.Register(id, alice);

        var token = _subject.Mint(alice.Id, id);

        token.Should().StartWith(ChatAccess.TokenPrefix);
        using var connection = _h.Workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chat_tokens WHERE token_hash = $token OR token_hash LIKE '%' || $token || '%'";
        command.Parameters.AddWithValue("$token", token);
        command.ExecuteScalar().Should().Be(0L);
    }

    [Fact]
    public void should_act_as_the_person_with_the_permissions_of_their_role_as_it_is_now()
    {
        var alice = _h.Reader("alice");
        var id = NewId();
        _subject.Register(id, alice);
        var token = _subject.Mint(alice.Id, id);

        var identity = _subject.Authenticate(token);

        identity.Should().NotBeNull();
        identity!.ConversationId.Should().Be(id);
        identity.Principal.Account.Id.Should().Be(alice.Id);
        identity.Principal.Permissions.Should().BeEquivalentTo(alice.Permissions);
        identity.Principal.Sources.Should().BeNull();
        identity.Principal.KeyId.Should().BeNull();
    }

    [Fact]
    public void should_stop_a_token_when_the_person_is_disabled()
    {
        var alice = _h.Reader("alice");
        var token = _subject.Mint(alice.Id, NewId());
        _subject.Authenticate(token).Should().NotBeNull();

        _h.Service.Update(alice.Id, Roles.Reader, enabled: false, "test");

        _subject.Authenticate(token).Should().BeNull("a disabled person has no hands in the chat");
    }

    [Fact]
    public void should_read_the_permissions_of_the_role_at_every_call_not_at_the_time_of_the_token()
    {
        var alice = _h.Reader("alice");
        var token = _subject.Mint(alice.Id, NewId());
        _subject.Authenticate(token)!.Principal.Permissions.Should().NotContain(Permissions.NotesWrite);

        _h.Service.Update(alice.Id, Roles.Admin, enabled: true, "test");

        _subject.Authenticate(token)!.Principal.Permissions.Should().Contain(Permissions.NotesWrite);
    }

    [Fact]
    public void should_not_give_the_hands_of_the_chat_to_a_service()
    {
        var robot = _h.Robot("bot", Roles.Reader);

        _subject.Authenticate(_subject.Mint(robot.Id, NewId())).Should().BeNull("the chat is for people");
    }

    [Theory]
    [InlineData("")]
    [InlineData("mnm_" + "x")]
    [InlineData("mnc_not-a-token-we-made")]
    public void should_know_no_token_it_did_not_make(string token)
    {
        _subject.Authenticate(token).Should().BeNull();
    }

    [Fact]
    public void should_let_a_token_live_thirty_minutes_from_the_last_message_of_its_conversation()
    {
        var alice = _h.Reader("alice");
        var id = NewId();
        var first = _subject.Mint(alice.Id, id);

        _h.Clock.Advance(TimeSpan.FromMinutes(29));
        _subject.Authenticate(first).Should().NotBeNull("29 minutes is inside the window");
        _h.Clock.Advance(TimeSpan.FromMinutes(2));
        _subject.Authenticate(first).Should().BeNull("31 minutes after it was made, with no message since");
    }

    [Fact]
    public void should_keep_the_earlier_tokens_of_a_conversation_alive_by_a_new_message_but_not_those_of_another()
    {
        var alice = _h.Reader("alice");
        var conversation = NewId();
        var first = _subject.Mint(alice.Id, conversation);
        var other = _subject.Mint(alice.Id, NewId());
        _h.Clock.Advance(TimeSpan.FromMinutes(20));

        _subject.Mint(alice.Id, conversation);
        _h.Clock.Advance(TimeSpan.FromMinutes(20));

        _subject.Authenticate(first).Should().NotBeNull();
        _subject.Authenticate(other).Should().BeNull();
    }

    [Fact]
    public void should_end_a_token_twelve_hours_after_it_was_made_whatever_is_sent_after()
    {
        var alice = _h.Reader("alice");
        var id = NewId();
        var first = _subject.Mint(alice.Id, id);
        for (var step = 0; step < 24; step++)
        {
            _h.Clock.Advance(TimeSpan.FromMinutes(29));
            _subject.Mint(alice.Id, id);
        }

        _subject.Authenticate(first).Should().NotBeNull("11 hours and 36 minutes in");
        _h.Clock.Advance(TimeSpan.FromMinutes(29));
        _subject.Mint(alice.Id, id);
        _h.Clock.Advance(TimeSpan.FromMinutes(29));
        _subject.Authenticate(first).Should().BeNull("past twelve hours");
    }

    [Fact]
    public void should_forget_the_tokens_that_have_expired_when_the_runner_is_listed()
    {
        var alice = _h.Reader("alice");
        var id = NewId();
        _subject.Register(id, alice);
        _subject.Mint(alice.Id, id);
        _h.Clock.Advance(TimeSpan.FromHours(1));

        _subject.Keep(new HashSet<string> { id });

        using var connection = _h.Workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chat_tokens";
        command.ExecuteScalar().Should().Be(0L);
    }
}
