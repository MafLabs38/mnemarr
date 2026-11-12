using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Notifications;

namespace Mnemarr.Api.V1.Notifications;

/// <summary>
/// Whether Mnemarr can write to a person, and how: <paramref name="Enabled"/>, the channel (<paramref name="Provider"/>), where it writes (<paramref name="Recipient"/>, known to the person who
/// manages the machines because they set it) and what happened to the last attempt (<paramref name="LastAt"/>, <paramref name="LastDelivered"/>, <paramref name="LastProblem"/>).
/// </summary>
public sealed record NotificationStatusResource(bool Enabled, string? Provider, string? Recipient, DateTimeOffset? LastAt, bool? LastDelivered, string? LastProblem);

/// <summary>The outcome of a test mail: whether it left and, if not, why.</summary>
public sealed record NotificationTestResource(bool Delivered, string? Problem);

/// <summary>The notifications (ADR 0042): to see that they work, and to try them. They are set by configuration, never from here.</summary>
[ApiController]
[Route("api/v1/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly AttentionMailer _mailer;

    public NotificationsController(AttentionMailer mailer) => _mailer = mailer;

    [HttpGet]
    [Authorize(Policy = Permissions.CollectorsManage)]
    public NotificationStatusResource Status()
    {
        var status = _mailer.Status();
        var last = status.Last;
        return new NotificationStatusResource(status.Enabled, status.Provider, status.Recipient, last?.At, last?.Delivered, last is { Delivered: false } ? last.Reason : null);
    }

    /// <summary>Sends a mail to the address that is set, to check the settings. It counts in the daily cap.</summary>
    [HttpPost("test")]
    [Authorize(Policy = Permissions.CollectorsManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<NotificationTestResource> Test(CancellationToken cancellationToken)
    {
        var result = await _mailer.SendTestAsync(cancellationToken);
        return new NotificationTestResource(result.Delivered, result.Reason);
    }
}
