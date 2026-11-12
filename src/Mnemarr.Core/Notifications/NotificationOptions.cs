using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Notifications;

/// <summary>
/// Settings of the notifications, from the <c>Notifications</c> section (ADR 0042). Off by default: turning it on lets Mnemarr write to one person's mailbox when a machine
/// asks for them, with a text of Mnemarr's own (never what a machine sent, never a conversation).
/// </summary>
public sealed class NotificationOptions
{
    public bool Enabled { get; set; }

    /// <summary>How the notification is delivered: the name of an <see cref="INotificationProvider"/>. Built in: <c>Smtp</c>.</summary>
    public string Provider { get; set; } = NotificationProviders.Smtp;

    /// <summary>The address the notifications go to. Explicit: nothing is ever sent to an address a machine or a note gave.</summary>
    public string? Recipient { get; set; }

    /// <summary>The address the notifications come from (often the one of the mailbox that sends them).</summary>
    public string? From { get; set; }

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Hours between two reminders while a machine still asks for attention.</summary>
    public int ReminderHours { get; set; } = 24;

    /// <summary>How many reminders follow the first mail of an episode, at most.</summary>
    public int MaxReminders { get; set; } = 3;

    /// <summary>The most mails tried in a day, all machines together: a compromised key must not become a mail cannon.</summary>
    public int MaxPerDay { get; set; } = 10;

    /// <summary>The most mails tried for one machine in a day, whether they went through or not.</summary>
    public int MaxPerMachinePerDay { get; set; } = 3;

    /// <summary>Minutes to wait after a mail that could not be sent, before trying again.</summary>
    public int RetryMinutes { get; set; } = 15;

    public int PassSeconds { get; set; } = 60;
}

/// <summary>The settings of the <c>Smtp</c> provider (<c>Notifications:Smtp</c>). Google: smtp.gmail.com, port 587, an application password.</summary>
public sealed class SmtpOptions
{
    public string? Host { get; set; }

    /// <summary>The port of the submission service, with STARTTLS (587). The implicit TLS of port 465 is not supported.</summary>
    public int Port { get; set; } = 587;

    public string? UserName { get; set; }

    /// <summary>A file that holds the password (a secret mounted as a file, never an environment variable). For Google, an application password.</summary>
    public string? PasswordFile { get; set; }

    /// <summary>Whether the connection is upgraded to TLS. Off only for a server without a password, such as a catcher on a test machine: a password is never sent in clear.</summary>
    public bool StartTls { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class NotificationOptionsValidator : IValidateOptions<NotificationOptions>
{
    private readonly INotificationProvider[] _providers;

    public NotificationOptionsValidator(IEnumerable<INotificationProvider> providers) => _providers = [.. providers];

    public ValidateOptionsResult Validate(string? name, NotificationOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!Address.IsValid(options.Recipient) || !Address.IsValid(options.From))
        {
            return ValidateOptionsResult.Fail("Notifications:Recipient (MNEMARR__NOTIFICATIONS__RECIPIENT) and Notifications:From (MNEMARR__NOTIFICATIONS__FROM) are needed, each one a mail address.");
        }

        var provider = NotificationProviders.Find(_providers, options.Provider);
        var failure = provider is null
            ? $"Notifications:Provider (MNEMARR__NOTIFICATIONS__PROVIDER) is one of: {string.Join(", ", _providers.Select(known => known.Name))}."
            : provider.Validate(options);
        if (failure is not null)
        {
            return ValidateOptionsResult.Fail(failure);
        }

        if (options.ReminderHours is < 1 or > 720 || options.MaxReminders is < 0 or > 30 || options.MaxPerDay is < 1 or > 1000 || options.MaxPerMachinePerDay is < 1 or > 100 || options.RetryMinutes is < 1 or > 1440 || options.PassSeconds is < 10 or > 3600)
        {
            return ValidateOptionsResult.Fail("Notifications: ReminderHours is 1 to 720, MaxReminders 0 to 30, MaxPerDay 1 to 1000, MaxPerMachinePerDay 1 to 100, RetryMinutes 1 to 1440, PassSeconds 10 to 3600.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal static class Address
{
    public static bool IsValid(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.All(character => !char.IsControl(character)) && System.Net.Mail.MailAddress.TryCreate(text, out var address) && address.Address == text.Trim();
}
