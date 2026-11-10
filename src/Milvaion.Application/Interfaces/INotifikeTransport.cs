using Milvaion.Application.Dtos.NotificationDtos;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Optional delivery of e-mails and in-app notifications through Notifike. Implementations report failures in the
/// result and never throw, so a Notifike outage cannot break the caller.
/// </summary>
public interface INotifikeTransport
{
    /// <summary>
    /// Whether e-mails should go through Notifike: enabled, fully configured and allowed in this environment.
    /// </summary>
    bool CanSendEmail { get; }

    /// <summary>
    /// Whether in-app notifications should go through Notifike: enabled, fully configured and allowed in this environment.
    /// </summary>
    bool CanSendInApp { get; }

    /// <summary>
    /// Sends an e-mail.
    /// </summary>
    /// <param name="email"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<NotifikeTransportResult> SendEmailAsync(NotifikeEmail email, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a notification in the boxes of users.
    /// </summary>
    /// <param name="notification"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<NotifikeTransportResult> SendInAppAsync(NotifikeInAppNotification notification, CancellationToken cancellationToken);
}
