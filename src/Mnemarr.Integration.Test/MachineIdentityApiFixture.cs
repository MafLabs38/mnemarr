using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
            .Where(item => item.GetProperty("machine").GetString() == machine).Select(item => item.GetProperty("conversations").GetInt32()).SingleOrDefault();

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
        (await client.PostAsJsonAsync("/api/v1/ingest/conversations", Batch(machine, "s2", Line("hello there my friend")), Token)).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
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
}
