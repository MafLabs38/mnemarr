using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Notifications;

/// <summary>A message for a person: where it goes, a short subject and a plain text.</summary>
public sealed record Notification(string Recipient, string Subject, string Body);

/// <summary>Whether a notification was handed over, and when it was not, why in a short sentence (never a secret).</summary>
public sealed record NotificationResult(bool Delivered, string? Reason = null)
{
    public static NotificationResult Done { get; } = new(true);

    public static NotificationResult Failed(string reason) => new(false, reason);
}

/// <summary>Delivers a notification to a person. The only thing the rest of Mnemarr knows about mail, a chat or a push.</summary>
public interface INotificationSender
{
    /// <summary>Never throws for a delivery that fails: it says so in the result. A cancelled call is the only exception.</summary>
    Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken);
}

/// <summary>
/// A way to deliver notifications, by name (<c>Notifications:Provider</c>). Another channel is one class that implements this and one line,
/// <c>services.AddSingleton&lt;INotificationProvider, MyProvider&gt;()</c>: it says what settings it needs and builds the <see cref="INotificationSender"/> that talks to it (ADR 0042).
/// </summary>
public interface INotificationProvider
{
    /// <summary>The value of <c>Notifications:Provider</c> that selects it; not case sensitive.</summary>
    string Name { get; }

    /// <summary>What is wrong with the settings for this provider, or null when they are right. Called only when notifications are turned on.</summary>
    string? Validate(NotificationOptions options);

    INotificationSender Create(IOptions<NotificationOptions> options);

    /// <summary>What is wrong with the secrets the provider needs (a file that cannot be read), or null. Looked at by the readiness probe, never sent anywhere.</summary>
    string? CheckSecrets(NotificationOptions options);
}

/// <summary>The providers that come with Mnemarr, and how one is found by name.</summary>
public static class NotificationProviders
{
    public const string Smtp = "Smtp";

    public static IReadOnlyList<INotificationProvider> BuiltIn { get; } = [new SmtpNotificationProvider()];

    public static INotificationProvider? Find(IEnumerable<INotificationProvider> providers, string? name) =>
        providers.FirstOrDefault(provider => string.Equals(provider.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static INotificationSender Select(IEnumerable<INotificationProvider> providers, IOptions<NotificationOptions> options) =>
        Find(providers, options.Value.Provider)?.Create(options) ?? new UnavailableNotificationSender(options.Value.Provider);
}

internal sealed class UnavailableNotificationSender(string provider) : INotificationSender
{
    public Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken) =>
        Task.FromResult(NotificationResult.Failed($"There is no notification provider called '{provider}'."));
}
