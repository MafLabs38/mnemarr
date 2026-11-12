using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mnemarr.Core.Notifications;

namespace Mnemarr.Integration.Test;

/// <summary>The notifications through the API (ADR 0042): off by default, a test mail with its outcome, and the mail for a machine that asks for a person, with what the page tells of it.</summary>
public sealed class NotificationsApiFixture : IClassFixture<MnemarrFactory>, IClassFixture<NotificationsFactory>
{
    private readonly MnemarrFactory _off;
    private readonly NotificationsFactory _on;

    public NotificationsApiFixture(MnemarrFactory off, NotificationsFactory on)
    {
        _off = off;
        _on = on;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(Callers Callers, HttpClient Machine, string Name)> SatelliteAsync()
    {
        var callers = await Callers.ForAsync(_on);
        var name = Callers.Unique("gemini");
        var created = await callers.Admin.PostAsJsonAsync("/api/v1/enrolments", new { machine = name, kind = "always-on" }, Token);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString();
        var exchanged = await _on.CreateBrowserClient().PostAsJsonAsync("/api/v1/ingest/enrol", new { code, machine = name }, Token);
        return (callers, _on.CreateKeyClient((await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("key").GetString()!), name);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient admin, string name) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/v1/collectors", Token)).EnumerateArray().Single(item => item.GetProperty("machine").GetString() == name);

    [Fact]
    public async Task should_say_that_the_notifications_are_off_and_refuse_a_test_mail()
    {
        var callers = await Callers.ForAsync(_off);

        var status = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", Token);
        var test = await (await callers.Admin.PostAsync("/api/v1/notifications/test", null, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);

        status.GetProperty("enabled").GetBoolean().Should().BeFalse();
        status.GetProperty("recipient").ValueKind.Should().Be(JsonValueKind.Null);
        test.GetProperty("delivered").GetBoolean().Should().BeFalse();
        test.GetProperty("problem").GetString().Should().Contain("off");
    }

    [Fact]
    public async Task should_need_the_right_to_manage_the_collectors()
    {
        var callers = await Callers.ForAsync(_on);

        (await callers.Anonymous.GetAsync("/api/v1/notifications", Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await callers["Reader"].GetAsync("/api/v1/notifications", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await callers["Reader"].PostAsync("/api/v1/notifications/test", null, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await callers["Operator"].GetAsync("/api/v1/notifications", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "only an administrator manages the collectors, and so the mails about them");
    }

    [Fact]
    public async Task should_send_a_test_mail_and_tell_the_outcome_of_the_last_attempt()
    {
        var callers = await Callers.ForAsync(_on);
        _on.Sender.FailWith = null;

        var delivered = await (await callers.Admin.PostAsync("/api/v1/notifications/test", null, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);

        delivered.GetProperty("delivered").GetBoolean().Should().BeTrue();
        _on.Sender.Sent.Should().Contain(mail => mail.Recipient == "me@homelab.example" && mail.Subject.Contains("test", StringComparison.Ordinal));
        var status = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", Token);
        status.GetProperty("enabled").GetBoolean().Should().BeTrue();
        status.GetProperty("recipient").GetString().Should().Be("me@homelab.example");
        status.GetProperty("lastDelivered").GetBoolean().Should().BeTrue();

        _on.Sender.FailWith = "The mail server could not be reached.";
        var failed = await (await callers.Admin.PostAsync("/api/v1/notifications/test", null, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        failed.GetProperty("problem").GetString().Should().Be("The mail server could not be reached.");
        (await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", Token)).GetProperty("lastProblem").GetString().Should().Be("The mail server could not be reached.");
        _on.Sender.FailWith = null;
    }

    [Fact]
    public async Task should_mail_once_for_a_machine_that_asks_and_show_it_on_its_line()
    {
        var (callers, machine, name) = await SatelliteAsync();
        _on.Sender.FailWith = null;
        var before = _on.Sender.Sent.Count(mail => mail.Subject.Contains(name, StringComparison.Ordinal));

        await machine.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.3.0", pendingLines = 0, attention = new { code = "needs-login", url = "https://satellite.homelab.example/connection" } }, Token);
        var mailer = _on.Services.GetRequiredService<AttentionMailer>();
        await mailer.RunOnceAsync(Token);
        await mailer.RunOnceAsync(Token);

        var mails = _on.Sender.Sent.Where(mail => mail.Subject.Contains(name, StringComparison.Ordinal)).ToList();
        mails.Should().HaveCount(before + 1);
        mails[^1].Body.Should().Contain($"{name} needs you to sign in again.").And.NotContain("satellite.homelab.example", "the address of a machine is not written in a mail");
        var attention = (await StatusAsync(callers.Admin, name)).GetProperty("attention");
        attention.GetProperty("mails").GetInt32().Should().Be(1);
        attention.GetProperty("mailProblem").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task should_show_why_the_mail_did_not_leave_on_the_line_of_the_machine()
    {
        var (callers, machine, name) = await SatelliteAsync();
        _on.Sender.FailWith = "The mail server could not be reached.";

        await machine.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { version = "0.3.0", pendingLines = 0, attention = new { code = "reader-broken" } }, Token);
        await _on.Services.GetRequiredService<AttentionMailer>().RunOnceAsync(Token);
        _on.Sender.FailWith = null;

        var attention = (await StatusAsync(callers.Admin, name)).GetProperty("attention");
        attention.GetProperty("mails").GetInt32().Should().Be(0);
        attention.GetProperty("mailProblem").GetString().Should().Be("The mail server could not be reached.");
    }
}
