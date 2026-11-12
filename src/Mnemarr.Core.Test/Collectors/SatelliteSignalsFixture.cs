using Mnemarr.Core.Collectors;

namespace Mnemarr.Core.Test.Collectors;

/// <summary>
/// What a satellite may ask of a person (ADR 0041): a code from a closed list and an address, both text from outside. The machine is failing while it asks, and the next heartbeat that does
/// not repeat the request ends the episode, with the mail that was sent for it.
/// </summary>
public sealed class SatelliteSignalsFixture : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly CollectorOptions Options = new() { OnlineWithinMinutes = 5, SilenceAfterMinutes = 30 };
    private readonly TestWorkspace _workspace = new();
    private readonly SqliteMachineStore _machines;

    public SatelliteSignalsFixture()
    {
        _machines = new SqliteMachineStore(_workspace.Database);
        _machines.Enrol(new Machine("gemini", MachineKind.AlwaysOn, Guid.NewGuid(), Now.AddDays(-1), null, null, 0, null, null));
    }

    public void Dispose() => _workspace.Dispose();

    private static Heartbeat Beat(HeartbeatAttention? attention = null, string? error = null) => new("0.3.0", 0, error, Attention: attention);

    [Theory]
    [InlineData("needs-login")]
    [InlineData("reader-broken")]
    public void should_keep_a_code_from_the_list_and_ignore_any_other(string code)
    {
        AttentionSignal.Clean(new HeartbeatAttention(code, "https://satellite.homelab.example/connection")).Should().Be(new HeartbeatAttention(code, "https://satellite.homelab.example/connection"));
        AttentionSignal.Clean(new HeartbeatAttention(code + "-x", "https://satellite.homelab.example")).Should().BeNull();
        AttentionSignal.Clean(null).Should().BeNull();
    }

    [Theory]
    [InlineData("https://satellite.homelab.example/connection", "https://satellite.homelab.example/connection")]
    [InlineData("http://127.0.0.1:8390/connection", "http://127.0.0.1:8390/connection")]
    [InlineData("http://localhost:3000/", "http://localhost:3000/")]
    [InlineData("http://satellite.homelab.example/connection", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,<script>alert(1)</script>", null)]
    [InlineData("ftp://satellite.homelab.example/", null)]
    [InlineData("https://user:secret@satellite.homelab.example/", null)]
    [InlineData("https:///nohost", null)]
    [InlineData("/relative/path", null)]
    [InlineData("https://satellite.homelab.example/a b", null)]
    [InlineData("https://satellite.homelab.example/\u0007", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void should_keep_an_address_only_if_it_is_https_or_http_to_this_host_with_a_host_and_nothing_else(string? url, string? expected)
    {
        AttentionSignal.CleanUrl(url).Should().Be(expected);
    }

    [Fact]
    public void should_drop_an_address_that_is_too_long_and_keep_the_code()
    {
        var url = "https://satellite.homelab.example/" + new string('a', AttentionSignal.MaxUrl);

        AttentionSignal.Clean(new HeartbeatAttention("needs-login", url)).Should().Be(new HeartbeatAttention("needs-login", null));
    }

    [Fact]
    public void should_say_a_sentence_of_its_own_for_each_code()
    {
        AttentionSignal.Sentence("needs-login").Should().Contain("sign in");
        AttentionSignal.Sentence("reader-broken").Should().Contain("look at it");
    }

    [Fact]
    public void should_make_a_machine_that_asks_for_a_person_failing_and_an_alert_until_it_stops_asking()
    {
        var asking = new Machine("gemini", MachineKind.AlwaysOn, Guid.NewGuid(), Now.AddDays(-1), Now.AddMinutes(-1), "0.3.0", 0, null, null, AttentionCode: "needs-login");
        var calm = asking with { AttentionCode = null };

        MachineStates.Of(asking, Now, Options).Should().Be(MachineState.Failing);
        MachineStates.IsAlert(asking, Now, Options).Should().BeTrue();
        MachineStates.Of(calm, Now, Options).Should().Be(MachineState.Online);
        MachineStates.IsAlert(calm, Now, Options).Should().BeFalse();
    }

    [Fact]
    public void should_start_an_episode_with_the_first_heartbeat_that_asks_and_keep_its_beginning_while_it_goes_on()
    {
        _machines.Seen("gemini", Now, Beat(new HeartbeatAttention("needs-login", "https://satellite.homelab.example/connection")));
        _machines.Seen("gemini", Now.AddMinutes(10), Beat(new HeartbeatAttention("needs-login", "https://satellite.homelab.example/connection")));

        var machine = _machines.Get("gemini")!;

        machine.Should().Match<Machine>(found => found.AttentionCode == "needs-login" && found.AttentionUrl == "https://satellite.homelab.example/connection" && found.AttentionSince == Now);
    }

    [Fact]
    public void should_end_the_episode_and_forget_its_mail_with_the_first_heartbeat_that_does_not_ask()
    {
        _machines.Seen("gemini", Now, Beat(new HeartbeatAttention("needs-login", null)));
        _machines.MarkAttentionMailed("gemini", Now.AddMinutes(1)).Should().BeTrue();
        _machines.Get("gemini")!.Should().Match<Machine>(found => found.AttentionMails == 1 && found.AttentionMailedAt == Now.AddMinutes(1));

        _machines.Seen("gemini", Now.AddMinutes(30), Beat());

        _machines.Get("gemini")!.Should().Match<Machine>(found => found.AttentionCode == null && found.AttentionUrl == null && found.AttentionSince == null && found.AttentionMailedAt == null && found.AttentionMails == 0);
    }

    [Fact]
    public void should_keep_the_mail_of_an_episode_when_the_heartbeat_repeats_the_request_and_not_count_a_mail_for_a_machine_that_asks_nothing()
    {
        _machines.MarkAttentionMailed("gemini", Now).Should().BeFalse("nothing is asked, nothing is mailed");
        _machines.Seen("gemini", Now, Beat(new HeartbeatAttention("reader-broken", null)));
        _machines.MarkAttentionMailed("gemini", Now.AddMinutes(1));

        _machines.Seen("gemini", Now.AddMinutes(2), Beat(new HeartbeatAttention("reader-broken", null)));

        _machines.Get("gemini")!.Should().Match<Machine>(found => found.AttentionMails == 1 && found.AttentionMailedAt == Now.AddMinutes(1) && found.AttentionSince == Now);
    }

    [Fact]
    public void should_leave_the_attention_alone_when_the_machine_is_only_seen_and_say_nothing_about_a_heartbeat_without_it()
    {
        _machines.Seen("gemini", Now, Beat(new HeartbeatAttention("needs-login", null)));

        _machines.Seen("gemini", Now.AddMinutes(1), null);

        _machines.Get("gemini")!.AttentionCode.Should().Be("needs-login");
    }

    [Fact]
    public void should_keep_what_a_person_said_about_consolidating_through_the_heartbeats_and_put_it_back_to_the_default_on_request()
    {
        _machines.Get("gemini")!.Consolidate.Should().BeNull();

        _machines.SetConsolidate("gemini", true).Should().BeTrue();
        _machines.Seen("gemini", Now, Beat());
        _machines.Get("gemini")!.Consolidate.Should().BeTrue();
        _machines.SetConsolidate("gemini", false);
        _machines.Get("gemini")!.Consolidate.Should().BeFalse();
        _machines.SetConsolidate("gemini", null);
        _machines.Get("gemini")!.Consolidate.Should().BeNull();
        _machines.SetConsolidate("nobody", true).Should().BeFalse();
    }
}
