using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Notifications;

namespace Mnemarr.Core.Test.Notifications;

/// <summary>
/// The policy of the mail for a machine that asks for a person (ADR 0042): one mail when the episode begins, a reminder at most once a day for a few days, nothing when it ends, nothing
/// twice after a restart, a bounded retry after a failure, two daily caps, and a text that is Mnemarr's own.
/// </summary>
public sealed class AttentionMailerFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteMachineStore _machines;
    private readonly SqliteNotificationLog _log;
    private readonly FakeSender _sender = new();
    private readonly NotificationOptions _settings = new() { Enabled = true, Recipient = "me@homelab.example", From = "mnemarr@homelab.example" };

    public AttentionMailerFixture()
    {
        _machines = new SqliteMachineStore(_workspace.Database);
        _log = new SqliteNotificationLog(_workspace.Database);
        Enrol("gemini");
    }

    public void Dispose() => _workspace.Dispose();

    private sealed class FakeSender : INotificationSender
    {
        public List<Notification> Sent { get; } = [];

        public string? FailWith { get; set; }

        public Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken)
        {
            if (FailWith is { } reason)
            {
                return Task.FromResult(NotificationResult.Failed(reason));
            }

            Sent.Add(notification);
            return Task.FromResult(NotificationResult.Done);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private AttentionMailer Mailer() => new(_machines, _sender, _log, Options.Create(_settings), _clock);

    private void Enrol(string name) => _machines.Enrol(new Machine(name, MachineKind.AlwaysOn, Guid.NewGuid(), _clock.GetUtcNow().AddDays(-1), null, null, 0, null, null));

    private void Ask(string name, string code = "needs-login", string? url = "https://satellite.homelab.example/connection") =>
        _machines.Seen(name, _clock.GetUtcNow(), new Heartbeat("0.3.0", 0, null, Attention: new HeartbeatAttention(code, url)));

    private void Calm(string name) => _machines.Seen(name, _clock.GetUtcNow(), new Heartbeat("0.3.0", 0, null));

    [Fact]
    public async Task should_send_one_mail_when_the_episode_begins_and_not_again_at_the_next_pass()
    {
        Ask("gemini");

        (await Mailer().RunOnceAsync(Token)).Should().Be(1);
        _clock.Advance(TimeSpan.FromMinutes(1));
        (await Mailer().RunOnceAsync(Token)).Should().Be(0);

        _sender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task should_write_to_the_address_that_is_set_in_mnemarrs_words_with_the_address_of_the_machine_and_nothing_else()
    {
        Ask("gemini", "needs-login", "https://satellite.homelab.example/connection");

        await Mailer().RunOnceAsync(Token);

        var mail = _sender.Sent.Single();
        mail.Recipient.Should().Be("me@homelab.example");
        mail.Subject.Should().Be("Mnemarr: gemini needs you");
        mail.Body.Should().Contain("gemini needs you to sign in again.").And.Contain("https://satellite.homelab.example/connection");
    }

    [Fact]
    public async Task should_not_write_a_link_when_the_machine_gave_no_acceptable_one()
    {
        Ask("gemini", "reader-broken", "http://not-secure.homelab.example/");

        await Mailer().RunOnceAsync(Token);

        _sender.Sent.Single().Body.Should().Contain("can no longer read").And.NotContain("http");
    }

    [Fact]
    public async Task should_not_send_again_after_a_restart()
    {
        Ask("gemini");
        await Mailer().RunOnceAsync(Token);

        // A new mailer over the same database: nothing is held in memory.
        _clock.Advance(TimeSpan.FromHours(1));
        (await Mailer().RunOnceAsync(Token)).Should().Be(0);

        _sender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task should_remind_once_a_day_and_at_most_the_number_of_reminders_that_is_set()
    {
        _settings.MaxReminders = 2;
        Ask("gemini");
        await Mailer().RunOnceAsync(Token);

        for (var day = 1; day <= 4; day++)
        {
            _clock.Advance(TimeSpan.FromHours(23));
            (await Mailer().RunOnceAsync(Token)).Should().Be(0, "less than a day since the last mail");
            _clock.Advance(TimeSpan.FromHours(1));
            await Mailer().RunOnceAsync(Token);
        }

        _sender.Sent.Should().HaveCount(3, "the first mail and two reminders");
        _sender.Sent[1].Subject.Should().EndWith("(reminder)");
        _sender.Sent[2].Body.Should().Contain("will not write again");
    }

    [Fact]
    public async Task should_send_nothing_when_the_episode_ends()
    {
        Ask("gemini");
        await Mailer().RunOnceAsync(Token);
        Calm("gemini");

        _clock.Advance(TimeSpan.FromDays(2));

        (await Mailer().RunOnceAsync(Token)).Should().Be(0);
        _sender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task should_send_nothing_for_a_machine_that_was_revoked()
    {
        Ask("gemini");
        _machines.Revoke("gemini", _clock.GetUtcNow());

        (await Mailer().RunOnceAsync(Token)).Should().Be(0);
    }

    [Fact]
    public async Task should_send_nothing_when_the_notifications_are_off()
    {
        _settings.Enabled = false;
        Ask("gemini");

        (await Mailer().RunOnceAsync(Token)).Should().Be(0);
    }

    [Fact]
    public async Task should_not_mark_the_mail_as_sent_when_it_failed_and_try_again_only_after_the_pause()
    {
        _sender.FailWith = "The mail server could not be reached.";
        Ask("gemini");

        (await Mailer().RunOnceAsync(Token)).Should().Be(0);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Mailer().RunOnceAsync(Token);
        _log.CountSince(_clock.GetUtcNow().AddDays(-1), "gemini").Should().Be(1, "the pause after a failure is not over");
        _log.Last("gemini").Should().Match<NotificationAttempt>(attempt => !attempt.Delivered && attempt.Reason == "The mail server could not be reached.");

        _sender.FailWith = null;
        _clock.Advance(TimeSpan.FromMinutes(11));
        (await Mailer().RunOnceAsync(Token)).Should().Be(1);
        _machines.Get("gemini")!.AttentionMails.Should().Be(1);
    }

    [Fact]
    public async Task should_stop_trying_for_a_machine_after_its_daily_cap_even_when_every_try_fails()
    {
        _sender.FailWith = "The mail server could not be reached.";
        _settings.MaxPerMachinePerDay = 3;
        Ask("gemini");

        for (var pass = 0; pass < 20; pass++)
        {
            await Mailer().RunOnceAsync(Token);
            _clock.Advance(TimeSpan.FromMinutes(20));
        }

        _log.CountSince(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), "gemini").Should().Be(3);
    }

    [Fact]
    public async Task should_not_let_a_machine_that_starts_and_stops_asking_become_a_mail_cannon()
    {
        _settings.MaxPerMachinePerDay = 2;
        for (var beat = 0; beat < 10; beat++)
        {
            Ask("gemini");
            await Mailer().RunOnceAsync(Token);
            Calm("gemini");
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _sender.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task should_stop_at_the_daily_cap_of_all_the_machines_together()
    {
        _settings.MaxPerDay = 3;
        for (var index = 0; index < 8; index++)
        {
            Enrol($"satellite-{index}");
            Ask($"satellite-{index}");
        }

        await Mailer().RunOnceAsync(Token);
        _clock.Advance(TimeSpan.FromHours(1));
        await Mailer().RunOnceAsync(Token);

        _sender.Sent.Should().HaveCount(3);
    }

    [Fact]
    public async Task should_try_again_the_next_day_under_the_caps()
    {
        _settings.MaxPerDay = 1;
        _settings.MaxReminders = 5;
        Ask("gemini");
        await Mailer().RunOnceAsync(Token);

        _clock.Advance(TimeSpan.FromHours(25));
        await Mailer().RunOnceAsync(Token);

        _sender.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task should_send_exactly_one_mail_and_no_reminder_when_none_is_set()
    {
        _settings.MaxReminders = 0;
        Ask("gemini");

        await Mailer().RunOnceAsync(Token);
        _clock.Advance(TimeSpan.FromDays(3));
        await Mailer().RunOnceAsync(Token);

        _sender.Sent.Should().ContainSingle();
        _sender.Sent[0].Subject.Should().NotContain("reminder");
    }

    [Fact]
    public async Task should_count_a_sender_that_throws_as_a_failed_attempt_so_that_the_pause_and_the_caps_hold()
    {
        var mailer = new AttentionMailer(_machines, new ThrowingSender(), _log, Options.Create(_settings), _clock);
        Ask("gemini");

        (await mailer.RunOnceAsync(Token)).Should().Be(0);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await mailer.RunOnceAsync(Token);

        _log.CountSince(_clock.GetUtcNow().AddDays(-1), "gemini").Should().Be(1, "one attempt, then the pause");
        _log.Last("gemini")!.Delivered.Should().BeFalse();
    }

    [Fact]
    public async Task should_not_make_a_new_episode_wait_for_a_failure_of_an_earlier_one()
    {
        _sender.FailWith = "The mail server could not be reached.";
        Ask("gemini");
        await Mailer().RunOnceAsync(Token);
        Calm("gemini");
        _clock.Advance(TimeSpan.FromMinutes(1));
        _sender.FailWith = null;

        Ask("gemini");

        (await Mailer().RunOnceAsync(Token)).Should().Be(1, "the pause after a failure belongs to the episode that failed");
    }

    private sealed class ThrowingSender : INotificationSender
    {
        public Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken) => throw new InvalidDataException("a sender that breaks its promise");
    }

    [Fact]
    public async Task should_send_a_test_mail_to_the_address_that_is_set_and_say_so_when_it_fails()
    {
        var mailer = Mailer();

        (await mailer.SendTestAsync(Token)).Delivered.Should().BeTrue();
        _sender.FailWith = "The mail server could not be reached.";
        var failed = await mailer.SendTestAsync(Token);

        failed.Should().Be(NotificationResult.Failed("The mail server could not be reached."));
        _sender.Sent.Single().Recipient.Should().Be("me@homelab.example");
        mailer.Status().Last.Should().Match<NotificationAttempt>(attempt => !attempt.Delivered && attempt.Machine == null);
    }

    [Fact]
    public async Task should_refuse_a_test_mail_when_the_notifications_are_off_or_the_daily_cap_is_reached()
    {
        _settings.MaxPerDay = 1;
        var mailer = Mailer();
        await mailer.SendTestAsync(Token);

        (await mailer.SendTestAsync(Token)).Delivered.Should().BeFalse();
        _settings.Enabled = false;
        (await mailer.SendTestAsync(Token)).Reason.Should().Contain("off");
        mailer.Status().Should().Be(new NotificationStatus(false, null, null, null));
    }

    [Fact]
    public void should_forget_old_attempts()
    {
        _log.Record("gemini", _clock.GetUtcNow().AddDays(-10), true, null);
        _log.Record("gemini", _clock.GetUtcNow(), true, null);

        _log.Prune(_clock.GetUtcNow().AddDays(-7));

        _log.CountSince(_clock.GetUtcNow().AddDays(-30)).Should().Be(1);
    }
}
