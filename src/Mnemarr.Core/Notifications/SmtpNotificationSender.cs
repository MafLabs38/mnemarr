using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Notifications;

public sealed class SmtpNotificationProvider : INotificationProvider
{
    public string Name => NotificationProviders.Smtp;

    public string? Validate(NotificationOptions options)
    {
        var smtp = options.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host) || smtp.Host.Any(char.IsWhiteSpace))
        {
            return "Notifications:Smtp:Host (MNEMARR__NOTIFICATIONS__SMTP__HOST) is needed: the mail server, such as smtp.gmail.com.";
        }

        if (smtp.Port is < 1 or > 65535 || smtp.TimeoutSeconds is < 1 or > 300)
        {
            return "Notifications:Smtp: Port is 1 to 65535 and TimeoutSeconds 1 to 300.";
        }

        if (smtp.Port == 465)
        {
            return "Notifications:Smtp:Port 465 (implicit TLS) is not supported: use 587, which upgrades the connection with STARTTLS.";
        }

        if (!string.IsNullOrWhiteSpace(smtp.UserName) != !string.IsNullOrWhiteSpace(smtp.PasswordFile))
        {
            return "Notifications:Smtp: UserName and PasswordFile go together (a file that holds the password).";
        }

        return !smtp.StartTls && !string.IsNullOrWhiteSpace(smtp.PasswordFile)
            ? "Notifications:Smtp:StartTls cannot be off when a password is used: it would be sent in clear."
            : null;
    }

    public INotificationSender Create(IOptions<NotificationOptions> options) => new SmtpNotificationSender(options);

    public string? CheckSecrets(NotificationOptions options)
    {
        var file = options.Smtp.PasswordFile;
        return string.IsNullOrWhiteSpace(file) || File.Exists(file) ? null : "The file of the mail password cannot be read.";
    }
}

/// <summary>Sends a notification as a mail, over SMTP with STARTTLS, with the framework's client (no dependency added). The password is read from its file at each send, so that it can be rotated.</summary>
public sealed class SmtpNotificationSender : INotificationSender
{
    private const int MaxReason = 200;
    private readonly IOptions<NotificationOptions> _options;

    public SmtpNotificationSender(IOptions<NotificationOptions> options) => _options = options;

    public async Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        var settings = _options.Value;
        var smtp = settings.Smtp;
        try
        {
            using var message = new MailMessage(new MailAddress(settings.From!), new MailAddress(notification.Recipient))
            {
                Subject = notification.Subject,
                Body = notification.Body,
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
            };
            // S5332 (a clear connection): STARTTLS is on unless the operator turns it off, and the validation refuses that with a password. It is only for a catcher on a test machine.
#pragma warning disable S5332
            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                EnableSsl = smtp.StartTls,
                Timeout = smtp.TimeoutSeconds * 1000,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };
#pragma warning restore S5332
            if (!string.IsNullOrWhiteSpace(smtp.UserName))
            {
                var password = (await File.ReadAllTextAsync(smtp.PasswordFile!, cancellationToken)).Trim();
                client.Credentials = new NetworkCredential(smtp.UserName, password);
            }

            await client.SendMailAsync(message, cancellationToken);
            return NotificationResult.Done;
        }
        catch (Exception ex) when (ex is SmtpException or IOException or SocketException or FormatException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Said in a short sentence the operator can act on; the exception itself may carry a server's text, which is not stored.
            return NotificationResult.Failed(Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var text = ex switch
        {
            SmtpException { StatusCode: SmtpStatusCode.MustIssueStartTlsFirst } => "The server wants a secure connection (turn Notifications:Smtp:StartTls on).",
            SmtpException { StatusCode: SmtpStatusCode.ClientNotPermitted } => "The server refused the sign-in: check the user name and the password (for Google, an application password).",
            SmtpException { InnerException: SocketException } or SocketException => "The mail server could not be reached.",
            SmtpException { StatusCode: SmtpStatusCode.GeneralFailure } => "The mail server could not be reached, or it refused the connection or the sign-in (for Google, use an application password).",
            SmtpException smtp => $"The mail server answered {smtp.StatusCode}.",
            IOException or UnauthorizedAccessException => "The file of the mail password cannot be read.",
            _ => "The mail could not be sent.",
        };
        return text.Length <= MaxReason ? text : text[..MaxReason];
    }
}
