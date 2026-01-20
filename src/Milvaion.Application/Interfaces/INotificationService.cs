using Milvaion.Application.Dtos.NotificationDtos;
using System.Linq.Expressions;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Manages in-app notifications.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Publishes a notification to a group of users matching a predicate.
    /// This method respects the users' 'AllowedNotifications' preferences.
    /// </summary>
    /// <param name="userExpression">LINQ expression to filter target users (e.g., u => u.UserType == UserType.Admin).</param>
    /// <param name="cancellationToken"></param>
    /// <param name="request">Notification request.</param>
    Task PublishAsync(InternalNotificationRequest request, Expression<Func<User, bool>> userExpression, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a specific notification as seen.
    /// </summary>
    /// <param name="notificationIdList">The ID list of the notification to mark as seen.</param>
    /// <param name="currentUserName">The ID of the user performing the action (must be the owner).</param>
    /// <param name="cancellationToken"></param>
    Task MarkAsSeenAsync(List<long> notificationIdList, string currentUserName, CancellationToken cancellationToken);

    /// <summary>
    /// Marks all unread notifications for a user as seen.
    /// </summary>
    /// <param name="currentUserName">The ID of the user performing the action.</param>
    /// <param name="cancellationToken"></param>
    Task MarkAllAsSeenAsync(string currentUserName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one or more notifications for the current user.
    /// </summary>
    /// <param name="notificationIdList">A list of notification IDs to delete.</param>
    /// <param name="currentUserName">The ID of the user performing the action (must be the owner).</param>
    /// <param name="cancellationToken"></param>
    Task DeleteAsync(IEnumerable<long> notificationIdList, string currentUserName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes all of notifications belonging to the current user.
    /// </summary>
    Task DeleteAllAsync(string currentUserName, CancellationToken cancellationToken);
}