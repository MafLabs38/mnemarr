using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Health;
using Mnemarr.Core.Notifications;

namespace Mnemarr.Core.Test.Notifications;

/// <summary>The SMTP provider: its settings, the mail it really sends (to a server of this test, on the loopback address), the failures it says in words, and the readiness probe.</summary>
public sealed class SmtpNotificationFixture : IDisposable
{
    private readonly string _passwordFile = Path.Combine(Path.GetTempPath(), $"mnemarr-smtp-{Guid.NewGuid():N}");

    public SmtpNotificationFixture() => File.WriteAllText(_passwordFile, "app-password\n");

    public void Dispose() => File.Delete(_passwordFile);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static NotificationOptions Settings(int port = 587, string host = "smtp.homelab.example") => new()
    {
        Enabled = true,
        Recipient = "me@homelab.example",
        From = "mnemarr@homelab.example",
        Smtp = new SmtpOptions { Host = host, Port = port, StartTls = true },
    };

    private static string? Problem(NotificationOptions options) =>
        new NotificationOptionsValidator(NotificationProviders.BuiltIn).Validate(null, options).FailureMessage;

    /// <summary>A server that says yes to everything, keeps what it was told and ends the conversation at QUIT.</summary>
    private sealed class MailServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public MailServer()
        {
            _listener.Start();
            Task = Task.Run(Serve);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task Task { get; }

        public StringBuilder Received { get; } = new();

        public void Dispose() => _listener.Stop();

        private async Task Serve()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 test ready");
            var inData = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                Received.AppendLine(line);
                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        await writer.WriteLineAsync("250 queued");
                    }

                    continue;
                }

                var command = line.Split(' ')[0].ToUpperInvariant();
                switch (command)
                {
                    case "EHLO" or "HELO":
                        await writer.WriteLineAsync("250 test");
                        break;
                    case "DATA":
                        inData = true;
                        await writer.WriteLineAsync("354 go on");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 ok");
                        break;
                }
            }
        }
    }

    [Fact]
    public async Task should_hand_the_mail_to_the_server_with_the_sender_the_recipient_the_subject_and_the_text()
    {
        using var server = new MailServer();
        var options = Settings(server.Port, "127.0.0.1");
        options.Smtp.StartTls = false;

        var result = await new SmtpNotificationSender(Options.Create(options)).SendAsync(new Notification("me@homelab.example", "Mnemarr: gemini needs you", "gemini needs you to sign in again."), Token);

        result.Should().Be(NotificationResult.Done);
        await server.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        server.Received.ToString().Should().Contain("MAIL FROM:<mnemarr@homelab.example>").And.Contain("RCPT TO:<me@homelab.example>").And.Contain("Subject: Mnemarr: gemini needs you").And.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes("gemini needs you to sign in again.")), "the text travels encoded");
    }

    [Fact]
    public async Task should_say_that_the_server_cannot_be_reached_in_a_sentence_without_the_exception()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var options = Settings(port, "127.0.0.1");
        options.Smtp.StartTls = false;

        var result = await new SmtpNotificationSender(Options.Create(options)).SendAsync(new Notification("me@homelab.example", "s", "b"), Token);

        result.Delivered.Should().BeFalse();
        result.Reason.Should().NotBeNullOrEmpty().And.NotContain("Exception").And.NotContain("127.0.0.1");
    }

    [Fact]
    public async Task should_let_go_of_a_server_that_accepts_the_connection_and_says_nothing()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var options = Settings(((IPEndPoint)silent.LocalEndpoint).Port, "127.0.0.1");
            options.Smtp.StartTls = false;
            options.Smtp.TimeoutSeconds = 1;

            var result = await new SmtpNotificationSender(Options.Create(options)).SendAsync(new Notification("me@homelab.example", "s", "b"), Token);

            result.Should().Be(NotificationResult.Failed("The mail server did not answer in time."));
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task should_say_that_the_file_of_the_password_cannot_be_read()
    {
        var options = Settings(2525, "127.0.0.1");
        options.Smtp.UserName = "mnemarr@homelab.example";
        options.Smtp.PasswordFile = Path.Combine(Path.GetTempPath(), "mnemarr-missing-" + Guid.NewGuid().ToString("N"));

        var result = await new SmtpNotificationSender(Options.Create(options)).SendAsync(new Notification("me@homelab.example", "s", "b"), Token);

        result.Should().Be(NotificationResult.Failed("The file of the mail password cannot be read."));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void should_say_in_a_sentence_what_the_operator_can_act_on(Exception failure, string contains)
    {
        SmtpNotificationSender.Describe(failure).Should().Contain(contains).And.NotContain("Exception");
    }

    public static TheoryData<Exception, string> Failures() => new()
    {
        { new SmtpException(SmtpStatusCode.MustIssueStartTlsFirst), "secure connection" },
        { new SmtpException(SmtpStatusCode.ClientNotPermitted), "sign-in" },
        { new SmtpException(SmtpStatusCode.GeneralFailure), "application password" },
        { new SmtpException(SmtpStatusCode.MailboxUnavailable), "MailboxUnavailable" },
        { new SmtpException("x", new SocketException()), "could not be reached" },
        { new System.Security.Authentication.AuthenticationException("the certificate is not trusted"), "certificate" },
        { new FormatException(), "could not be sent" },
    };

    [Fact]
    public void should_accept_the_settings_of_a_google_mailbox()
    {
        var options = Settings(587, "smtp.gmail.com");
        options.Smtp.UserName = "me@homelab.example";
        options.Smtp.PasswordFile = _passwordFile;

        Problem(options).Should().BeNull();
    }

    [Fact]
    public void should_accept_anything_when_the_notifications_are_off()
    {
        Problem(new NotificationOptions { Enabled = false, Recipient = "not an address" }).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "mnemarr@homelab.example")]
    [InlineData("me@homelab.example", null)]
    [InlineData("not an address", "mnemarr@homelab.example")]
    [InlineData("me@homelab.example\r\nBcc: other@homelab.example", "mnemarr@homelab.example")]
    [InlineData("Me <me@homelab.example>", "mnemarr@homelab.example")]
    public void should_need_two_plain_addresses(string? recipient, string? from)
    {
        var options = Settings();
        options.Recipient = recipient;
        options.From = from;

        Problem(options).Should().Contain("Recipient");
    }

    [Fact]
    public void should_name_the_providers_that_exist_when_the_name_is_not_one_of_them()
    {
        var options = Settings();
        options.Provider = "Pigeon";

        Problem(options).Should().Contain("Smtp");
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    public void should_need_a_host(string host)
    {
        var options = Settings();
        options.Smtp.Host = host;

        Problem(options).Should().Contain("Host");
    }

    [Fact]
    public void should_explain_why_the_implicit_tls_port_is_not_supported()
    {
        Problem(Settings(465)).Should().Contain("587");
    }

    [Fact]
    public void should_need_the_user_and_the_password_file_together()
    {
        var options = Settings();
        options.Smtp.UserName = "me@homelab.example";

        Problem(options).Should().Contain("go together");
    }

    [Fact]
    public void should_never_send_a_password_in_clear()
    {
        var options = Settings();
        options.Smtp.UserName = "me@homelab.example";
        options.Smtp.PasswordFile = _passwordFile;
        options.Smtp.StartTls = false;

        Problem(options).Should().Contain("in clear");
    }

    [Fact]
    public void should_refuse_values_out_of_range()
    {
        var options = Settings();
        options.MaxPerDay = 0;

        Problem(options).Should().Contain("MaxPerDay");
    }

    [Fact]
    public async Task should_be_healthy_when_off_and_degraded_with_the_reason_when_it_is_on_and_cannot_work()
    {
        using var workspace = new TestWorkspace();
        var log = new SqliteNotificationLog(workspace.Database);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        var options = Settings();
        options.Smtp.UserName = "me@homelab.example";
        options.Smtp.PasswordFile = _passwordFile;
        HealthCheckResult Check() => new NotificationsHealthCheck(Options.Create(options), NotificationProviders.BuiltIn, log).CheckHealthAsync(new HealthCheckContext(), Token).GetAwaiter().GetResult();

        options.Enabled = false;
        Check().Status.Should().Be(HealthStatus.Healthy);
        options.Enabled = true;
        Check().Status.Should().Be(HealthStatus.Healthy);

        log.Record("gemini", clock.GetUtcNow(), false, "The mail server could not be reached.");
        var failing = Check();
        failing.Status.Should().Be(HealthStatus.Degraded);
        failing.Data[HealthReasons.Key].Should().Be("notifications-failing");

        log.Record("gemini", clock.GetUtcNow(), true, null);
        Check().Status.Should().Be(HealthStatus.Healthy);

        File.Delete(_passwordFile);
        var secret = Check();
        secret.Status.Should().Be(HealthStatus.Degraded);
        secret.Data[HealthReasons.Key].Should().Be("notifications-secret");
        await File.WriteAllTextAsync(_passwordFile, "app-password", Token);
    }
}
