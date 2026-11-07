using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Integration.Test;

/// <summary>A machine is a computer, not a name (ADR 0031): the same computer under another name is the same machine; a machine is renamed, merged and deleted with what it sent.</summary>
public sealed class MachineIdentityApiFixture : EnrolmentFixtureBase, IClassFixture<EnrolledOnlyFactory>
{
    public MachineIdentityApiFixture(EnrolledOnlyFactory factory)
        : base(factory)
    {
    }

    private static string ComputerId(string seed) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed + Guid.NewGuid())));

    private async Task<string> JoinAsComputerAsync(HttpClient admin, string machine, string computerId)
    {
        var code = (await AddAsync(admin, machine)).GetProperty("code").GetString();
        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine, computerId }, Token);
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("machine").GetString().Should().Be(machine);
        return body.GetProperty("key").GetString()!;
    }

    private async Task<int> ConversationsOfAsync(HttpClient admin, string machine) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token)).EnumerateArray()
            .Single(item => item.GetProperty("machine").GetString() == machine).GetProperty("conversations").GetInt32();

    private async Task<bool> ListsAsync(HttpClient admin, string machine) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token)).EnumerateArray().Any(item => item.GetProperty("machine").GetString() == machine);

    [Fact]
    public async Task should_keep_one_machine_when_the_same_computer_joins_again_under_another_name_and_file_what_it_sent_under_the_new_name()
    {
        var callers = await Callers.ForAsync(_factory);
        var typo = Callers.Unique("gamng");
        var right = Callers.Unique("gaming");
        var computer = ComputerId("pc");
        using (var first = _factory.CreateKeyClient(await JoinAsComputerAsync(callers.Admin, typo, computer)))
        {
            (await first.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(typo, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        _factory.Clock.Advance(MachineJoining.LiveWindow + TimeSpan.FromSeconds(1));
        var secondKey = await JoinAsComputerAsync(callers.Admin, right, computer);

        (await ListsAsync(callers.Admin, typo)).Should().BeFalse("the computer is the same machine: it has the name it asked for now, not a second row");
        (await ConversationsOfAsync(callers.Admin, right)).Should().Be(1, "what it sent comes with it");
        using var second = _factory.CreateKeyClient(secondKey);
        var again = await second.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(right, "s1", Line("hello there my friend")), Token);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ConversationsOfAsync(callers.Admin, right)).Should().Be(1, "the same conversation is not made twice");
    }

    [Fact]
    public async Task should_refuse_a_name_that_another_machine_has_when_a_known_computer_asks_for_it()
    {
        var callers = await Callers.ForAsync(_factory);
        var one = Callers.Unique("one");
        var other = Callers.Unique("other");
        var computer = ComputerId("pc");
        await JoinAsComputerAsync(callers.Admin, one, computer);
        await JoinAsComputerAsync(callers.Admin, other, ComputerId("other-pc"));

        var code = (await AddAsync(callers.Admin, other)).GetProperty("code").GetString();
        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine = other, computerId = computer }, Token);

        exchanged.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ListsAsync(callers.Admin, one)).Should().BeTrue();
    }

    [Fact]
    public async Task should_refuse_an_identifier_that_is_not_the_hash_of_a_computer()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        var code = (await AddAsync(callers.Admin, machine)).GetProperty("code").GetString();

        var exchanged = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine, computerId = "my-laptop" }, Token);

        exchanged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_rename_a_machine_and_its_conversations_and_refuse_a_name_that_is_taken()
    {
        var callers = await Callers.ForAsync(_factory);
        var name = Callers.Unique("old");
        var newName = Callers.Unique("new");
        var taken = Callers.Unique("taken");
        using var client = await JoinAsync(callers.Admin, name);
        await JoinAsync(callers.Admin, taken);
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(name, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await callers.Admin.PostAsJsonAsync($"/api/v1/collectors/{name}/rename", new { name = taken }, Token)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await callers.Admin.PostAsJsonAsync($"/api/v1/collectors/{name}/rename", new { name = newName }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ListsAsync(callers.Admin, name)).Should().BeFalse();
        (await ConversationsOfAsync(callers.Admin, newName)).Should().Be(1);
    }

    [Fact]
    public async Task should_merge_a_name_that_is_no_longer_enrolled_into_a_machine_and_not_double_what_both_hold()
    {
        var callers = await Callers.ForAsync(_factory);
        var ghost = Callers.Unique("ghost");
        var machine = Callers.Unique("pc");
        using var client = await JoinAsync(callers.Admin, ghost);
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(ghost, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(ghost, "s2", Line("a second one here")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var other = await JoinAsync(callers.Admin, machine);
        (await other.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await callers.Admin.PostAsync($"/api/v1/collectors/{ghost}/remove", null, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StatusOfAsync(callers.Admin, ghost)).GetProperty("state").GetString().Should().Be("unenrolled");

        var merged = await callers.Admin.PostAsync($"/api/v1/collectors/{ghost}/merge", JsonContent.Create(new { into = machine }), Token);

        merged.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await merged.Content.ReadFromJsonAsync<JsonElement>(Token);
        (report.GetProperty("moved").GetInt32(), report.GetProperty("alreadyThere").GetInt32()).Should().Be((1, 1));
        (await ListsAsync(callers.Admin, ghost)).Should().BeFalse();
        (await ConversationsOfAsync(callers.Admin, machine)).Should().Be(2, "the one both had is kept once");
    }

    [Fact]
    public async Task should_take_a_machine_out_of_the_list_without_revoking_it_first_and_keep_what_it_sent()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        using var client = await JoinAsync(callers.Admin, machine);
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await callers.Admin.PostAsync($"/api/v1/collectors/{machine}/remove", null, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await StatusOfAsync(callers.Admin, machine)).GetProperty("state").GetString().Should().Be("unenrolled");
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s2", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "its key is revoked");
        (await ConversationsOfAsync(callers.Admin, machine)).Should().Be(1, "what it sent stays");
        var again = await callers.Admin.PostAsync($"/api/v1/collectors/{machine}/remove", null, Token);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound, "there is no machine to remove any more");
    }

    [Fact]
    public async Task should_delete_a_machine_with_everything_it_sent_and_leave_the_others()
    {
        var callers = await Callers.ForAsync(_factory);
        var gone = Callers.Unique("gone");
        var kept = Callers.Unique("kept");
        using var one = await JoinAsync(callers.Admin, gone);
        using var two = await JoinAsync(callers.Admin, kept);
        (await one.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(gone, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await two.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(kept, "s9", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        var erased = await callers.Admin.PostAsync($"/api/v1/collectors/{gone}/erase", null, Token);

        erased.StatusCode.Should().Be(HttpStatusCode.OK);
        (await erased.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("conversations").GetInt32().Should().Be(1);
        (await ListsAsync(callers.Admin, gone)).Should().BeFalse("neither the machine nor its name remains");
        (await ConversationsOfAsync(callers.Admin, kept)).Should().Be(1);
        (await callers.Admin.PostAsync($"/api/v1/collectors/{gone}/erase", null, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_tell_a_person_who_approves_a_link_what_the_computer_already_is()
    {
        var callers = await Callers.ForAsync(_factory);
        var machine = Callers.Unique("pc");
        var computer = ComputerId("pc");
        await JoinAsComputerAsync(callers.Admin, machine, computer);
        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var started = await (await _factory.CreateBrowserClient().PostAsJsonAsync("/api/v1/ingest/pair", new { hostname = "Gaming PC", computerId = computer }, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);

        var found = await callers.Admin.PostAsJsonAsync("/api/v1/pairings/lookup", new { code = started.GetProperty("userCode").GetString() }, Token);

        (await found.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("knownAs").GetString().Should().Be(machine);
    }

    private static Task<HttpResponseMessage> BeatAsync(HttpClient machine, string? computerId) =>
        machine.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.0.0", pendingLines = 0, computerId }, Token);

    [Fact]
    public async Task should_tell_the_person_at_approval_that_the_name_cannot_be_given_and_leave_the_request_waiting()
    {
        var callers = await Callers.ForAsync(_factory);
        var mine = Callers.Unique("mine");
        var other = Callers.Unique("other");
        var computer = ComputerId("pc");
        await JoinAsComputerAsync(callers.Admin, mine, computer);
        await JoinAsComputerAsync(callers.Admin, other, ComputerId("other-pc"));
        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var started = await (await _factory.CreateBrowserClient().PostAsJsonAsync("/api/v1/ingest/pair", new { hostname = "Gaming PC", computerId = computer }, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var found = await callers.Admin.PostAsJsonAsync("/api/v1/pairings/lookup", new { code = started.GetProperty("userCode").GetString() }, Token);
        var id = (await found.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();

        var approved = await callers.Admin.PostAsJsonAsync($"/api/v1/pairings/{id}/approve", new { code = started.GetProperty("userCode").GetString(), machine = other, kind = "intermittent" }, Token);

        approved.StatusCode.Should().Be(HttpStatusCode.Conflict, "the computer is already another machine here, and the name asked for is a third");
        (await callers.Admin.PostAsJsonAsync("/api/v1/pairings/lookup", new { code = started.GetProperty("userCode").GetString() }, Token)).StatusCode.Should().Be(HttpStatusCode.OK, "the request is still waiting: the person may name it again");
    }

    [Fact]
    public async Task should_not_use_up_a_code_for_a_join_that_cannot_be_done()
    {
        var callers = await Callers.ForAsync(_factory);
        var mine = Callers.Unique("mine");
        var other = Callers.Unique("other");
        var computer = ComputerId("pc");
        await JoinAsComputerAsync(callers.Admin, mine, computer);
        await JoinAsComputerAsync(callers.Admin, other, ComputerId("other-pc"));
        var code = (await AddAsync(callers.Admin, other)).GetProperty("code").GetString();

        var refused = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine = other, computerId = computer }, Token);
        var again = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine = other }, Token);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        again.StatusCode.Should().Be(HttpStatusCode.OK, "the code was not used up by the join that could not be done, so the key is not lost");
    }

    [Fact]
    public async Task should_give_a_machine_that_had_no_identifier_the_one_of_its_computer_and_never_one_that_another_machine_holds()
    {
        var callers = await Callers.ForAsync(_factory);
        var first = Callers.Unique("first");
        var second = Callers.Unique("second");
        using var one = await JoinAsync(callers.Admin, first);
        using var two = await JoinAsync(callers.Admin, second);
        var computer = ComputerId("pc");

        await BeatAsync(one, computer);
        await BeatAsync(two, computer);

        // The first machine holds the identifier now, so a reinstall of that computer under another name finds it.
        var renamed = Callers.Unique("renamed");
        _factory.Clock.Advance(MachineJoining.LiveWindow + TimeSpan.FromSeconds(1));
        await JoinAsComputerAsync(callers.Admin, renamed, computer);
        (await ListsAsync(callers.Admin, first)).Should().BeFalse("the first machine was found by the identifier its heartbeat gave");
        (await ListsAsync(callers.Admin, second)).Should().BeTrue("the second did not take an identifier that was held");
        (await ListsAsync(callers.Admin, renamed)).Should().BeTrue();
    }

    [Fact]
    public async Task should_let_a_computer_take_back_what_it_sent_under_a_machine_that_was_removed_and_nobody_else()
    {
        var callers = await Callers.ForAsync(_factory);
        var before = Callers.Unique("before");
        var after = Callers.Unique("after");
        var stranger = Callers.Unique("stranger");
        var computer = ComputerId("pc");
        var session = $"s-{before}";
        using (var one = _factory.CreateKeyClient(await JoinAsComputerAsync(callers.Admin, before, computer)))
        {
            (await one.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(before, session, Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await callers.Admin.PostAsync($"/api/v1/collectors/{before}/remove", null, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var thief = await JoinAsync(callers.Admin, stranger);
        (await thief.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(stranger, session, Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ConversationsOfAsync(callers.Admin, before)).Should().Be(1, "a key that only knows the session id took nothing over");
        using var back = _factory.CreateKeyClient(await JoinAsComputerAsync(callers.Admin, after, computer));
        (await BeatAsync(back, computer)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await back.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(after, session, Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListsAsync(callers.Admin, before)).Should().BeFalse("the same computer took its conversation back: nothing is left under the old name");
        (await ConversationsOfAsync(callers.Admin, after)).Should().Be(1);
    }

    [Fact]
    public async Task should_go_on_accepting_a_collector_that_sends_the_name_it_had_before_it_was_renamed_and_file_what_it_sends_under_the_new_name()
    {
        var callers = await Callers.ForAsync(_factory);
        var before = Callers.Unique("before");
        var after = Callers.Unique("after");
        using var client = await JoinAsync(callers.Admin, before);
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(before, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await callers.Admin.PostAsJsonAsync($"/api/v1/collectors/{before}/rename", new { name = after }, Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var old = await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(before, "s2", Line("a second conversation here")), Token);
        var current = await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(after, "s3", Line("a third conversation here")), Token);

        old.StatusCode.Should().Be(HttpStatusCode.OK, "the key says who it is: the name in its own configuration is the one it had");
        current.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListsAsync(callers.Admin, before)).Should().BeFalse("what it sends is filed under the name the machine has now");
        (await ConversationsOfAsync(callers.Admin, after)).Should().Be(3);
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch("a-stranger", "s4", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a name that was never its own is still refused");
    }

    [Fact]
    public async Task should_not_hand_a_machine_that_is_sending_right_now_to_whoever_says_its_identifier_and_do_when_it_has_stopped()
    {
        var callers = await Callers.ForAsync(_factory);
        var first = Callers.Unique("first");
        var second = Callers.Unique("second");
        var computer = ComputerId("pc");
        using (var one = _factory.CreateKeyClient(await JoinAsComputerAsync(callers.Admin, first, computer)))
        {
            (await BeatAsync(one, computer)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        var code = (await AddAsync(callers.Admin, second)).GetProperty("code").GetString();
        var live = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine = second, computerId = computer }, Token);
        _factory.Clock.Advance(MachineJoining.LiveWindow + TimeSpan.FromSeconds(1));
        var stopped = await _factory.CreateBrowserClient().PostAsJsonAsync(Enrol, new { code, machine = second, computerId = computer }, Token);

        live.StatusCode.Should().Be(HttpStatusCode.Conflict, "its collector sent a sign of life a moment ago");
        stopped.StatusCode.Should().Be(HttpStatusCode.OK, "the code was not used up by the refused join, and the machine has stopped");
        (await ListsAsync(callers.Admin, first)).Should().BeFalse();
    }

    [Fact]
    public async Task should_not_merge_into_a_machine_that_is_revoked()
    {
        var callers = await Callers.ForAsync(_factory);
        var ghost = Callers.Unique("ghost");
        var revoked = Callers.Unique("revoked");
        using var client = await JoinAsync(callers.Admin, ghost);
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(ghost, "s1", Line("hello there my friend")), Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        await JoinAsync(callers.Admin, revoked);
        (await callers.Admin.DeleteAsync($"/api/v1/collectors/{revoked}", Token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await callers.Admin.PostAsJsonAsync($"/api/v1/collectors/{ghost}/merge", new { into = revoked }, Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ConversationsOfAsync(callers.Admin, ghost)).Should().Be(1, "nothing was moved");
    }
}
