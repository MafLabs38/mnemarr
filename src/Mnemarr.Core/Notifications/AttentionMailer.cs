using Microsoft.Extensions.Options;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Core.Notifications;

/// <summary>Whether notifications are on, by which channel and to whom, and the last attempt (null when none was made).</summary>
public sealed record NotificationStatus(bool Enabled, string? Provider, string? Recipient, NotificationAttempt? Last);

/// <summary>
/// Tells a person that a machine asks for them (ADR 0042): one mail when the episode begins, a reminder at most once a day for a few days while it lasts, nothing when it ends.
/// What is stored (the mails of the episode, when the last one left) means a restart sends nothing twice. The text is Mnemarr's own, made from the closed code of the signal; the only
/// thing a machine contributes is its name, a restricted charset: not even the address it gave is written in the mail. Only a machine that is enrolled and not revoked is ever mailed, and two caps hold whatever a machine does.
/// </summary>
public sealed class AttentionMailer
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private readonly IMachineStore _machines;
    private readonly INotificationSender _sender;
    private readonly INotificationLog _log;
    private readonly IOptions<NotificationOptions> _options;
    private readonly IOptions<CollectorOptions> _collectors;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _one = new(1, 1);

    public AttentionMailer(IMachineStore machines, INotificationSender sender, INotificationLog log, IOptions<NotificationOptions> options, IOptions<CollectorOptions> collectors, TimeProvider clock)
    {
        _collectors = collectors;
        _machines = machines;
        _sender = sender;
        _log = log;
        _options = options;
        _clock = clock;
    }

    /// <summary>One pass: the mails that are due. Returns how many left.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return 0;
        }

        // One look at the caps and one send at a time, whoever asks (the pass, a test mail): the caps are read, then the attempt is written.
        await _one.WaitAsync(cancellationToken);
        try
        {
            return await SendDueAsync(options, cancellationToken);
        }
        finally
        {
            _ = _one.Release();
        }
    }

    private async Task<int> SendDueAsync(NotificationOptions options, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var sent = 0;
        foreach (var machine in _machines.List().Where(machine => machine.AttentionCode is not null && machine.RevokedAt is null))
        {
            if (!IsDue(machine, options, now) || !MayTry(machine.Name, machine.AttentionSince ?? DateTimeOffset.MinValue, options, now))
            {
                continue;
            }

            if (_log.CountSince(now - Day) >= options.MaxPerDay)
            {
                break;
            }

            var result = await SendAsync(Compose(machine, options), cancellationToken);
            _log.Record(machine.Name, now, result.Delivered, result.Reason);
            if (result.Delivered)
            {
                _machines.MarkAttentionMailed(machine.Name, now);
                sent++;
            }
        }

        _log.Prune(now - TimeSpan.FromDays(7));
        return sent;
    }

    /// <summary>Whether notifications are on, where they go, and what happened to the last attempt.</summary>
    public NotificationStatus Status()
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return new NotificationStatus(false, null, null, null);
        }

        return new NotificationStatus(true, options.Provider, options.Recipient, _log.Last());
    }

    /// <summary>A mail to see whether the settings work. It counts in the daily cap of all mails, and says why when it fails.</summary>
    public async Task<NotificationResult> SendTestAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return NotificationResult.Failed("Notifications are off (Notifications:Enabled).");
        }

        await _one.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            if (_log.CountSince(now - Day) >= options.MaxPerDay)
            {
                return NotificationResult.Failed("The daily limit of mails is reached.");
            }

            var result = await SendAsync(new Notification(options.Recipient!, "Mnemarr: a test mail", "This mail was sent by Mnemarr to check that the notifications work. Nothing else is needed."), cancellationToken);
            _log.Record(null, now, result.Delivered, result.Reason);
            return result;
        }
        finally
        {
            _ = _one.Release();
        }
    }

    /// <summary>A sender is asked not to throw, but one that does is a failed attempt like any other: it is recorded, so that the caps and the pause hold.</summary>
    private async Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        try
        {
            return await _sender.SendAsync(notification, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotificationResult.Failed("The mail could not be sent.");
        }
    }

    private static bool IsDue(Machine machine, NotificationOptions options, DateTimeOffset now)
    {
        if (machine.AttentionMails == 0 || machine.AttentionMailedAt is null)
        {
            return true;
        }

        return machine.AttentionMails <= options.MaxReminders && now - machine.AttentionMailedAt.Value >= TimeSpan.FromHours(options.ReminderHours);
    }

    /// <summary>The caps of one machine and the pause after a mail that failed: a bounded retry, never a tight loop.</summary>
    private bool MayTry(string machine, DateTimeOffset episodeStart, NotificationOptions options, DateTimeOffset now)
    {
        if (_log.CountSince(now - Day, machine) >= options.MaxPerMachinePerDay)
        {
            return false;
        }

        // Only a failure of this episode makes it wait: one from an earlier episode is not the reason for a pause now.
        var last = _log.Last(machine);
        return last is null || last.Delivered || last.At < episodeStart || now - last.At >= TimeSpan.FromMinutes(options.RetryMinutes);
    }

    private Notification Compose(Machine machine, NotificationOptions options)
    {
        var reminder = machine.AttentionMails > 0;
        var subject = $"Mnemarr: {machine.Name} needs you" + (reminder ? " (reminder)" : string.Empty);
        var body = new System.Text.StringBuilder()
            .Append(machine.Name).Append(' ').AppendLine(AttentionSignal.Sentence(machine.AttentionCode!));
        // The address a satellite gave is not in the mail: it is text from a machine, and a mail comes from the operator's own mailbox. The mail points to the Machines page of Mnemarr, where
        // the person is signed in and the link is shown with its host.
        body.AppendLine().Append("Open the Machines page of Mnemarr to see it");
        if (_collectors.Value.PublicUrl is { Length: > 0 } publicUrl)
        {
            body.AppendLine(":").AppendLine(publicUrl.TrimEnd('/') + "/machines");
        }
        else
        {
            body.AppendLine(".");
        }

        body.AppendLine().Append("Mnemarr sends this when a machine you enrolled asks for you. ");
        body.Append(machine.AttentionMails < options.MaxReminders ? "It reminds you again if nothing changes." : "It will not write again about this.");
        return new Notification(options.Recipient!, subject, body.ToString());
    }
}
