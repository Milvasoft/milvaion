using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.NotificationDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.Domain;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.DataAccess.EfCore.Utils;
using System.Linq.Expressions;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Provides functionality for managing and delivering notifications to users.
/// </summary>
/// <remarks>The <see cref="NotificationService"/> class allows for publishing notifications to users based on
/// specific criteria, marking notifications as seen, and deleting notifications. It supports both targeted
/// notifications to specific users and notifications based on user-defined predicates. This service is designed to work
/// with a repository pattern for data access and assumes the use of dependency injection for its
/// dependencies.</remarks>
/// <param name="notificationRepository"></param>
/// <param name="userRepository"></param>
/// <param name="logger"></param>
public class NotificationService(IMilvaionRepositoryBase<InternalNotification> notificationRepository,
                                 IMilvaionRepositoryBase<User> userRepository,
                                 Lazy<IMilvaLogger> logger) : INotificationService
{
    private readonly IMilvaionRepositoryBase<InternalNotification> _notificationRepository = notificationRepository;
    private readonly IMilvaionRepositoryBase<User> _userRepository = userRepository;
    private readonly Lazy<IMilvaLogger> _logger = logger;
    private static readonly JsonSerializerOptions _safeJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 16,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    /// <summary>
    /// Publishes a notification based on a user predicate.
    /// This is the core publishing method.
    /// </summary>
    public async Task PublishAsync(InternalNotificationRequest request, Expression<Func<User, bool>> userExpression, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Find target users based on the expression
            if (request.FindRecipientsFromType)
            {
                var jsonValue = JsonSerializer.Serialize(request.Type);

                userExpression = userExpression == null
                    ? u => NpgsqlJsonDbFunctionsExtensions.JsonContains(EF.Functions, u.AllowedNotifications, jsonValue)
                    : userExpression.AndAlso(u => NpgsqlJsonDbFunctionsExtensions.JsonContains(EF.Functions, u.AllowedNotifications, jsonValue));
            }

            if (!request.Recipients.IsNullOrEmpty())
                userExpression = userExpression == null
                    ? u => request.Recipients.Contains(u.UserName)
                    : userExpression.Append(u => request.Recipients.Contains(u.UserName), ExpressionType.AndAlso);

            var usersToNotify = await _userRepository.GetAllAsync(userExpression, User.Projections.CreateNotification, tracking: false, cancellationToken: cancellationToken);

            if (usersToNotify.IsNullOrEmpty())
                return;

            var newNotifications = new List<InternalNotification>();

            var jsonData = request.Data != null ? JsonSerializer.Serialize(request.Data, _safeJsonOptions) : null;

            foreach (var userToNotify in usersToNotify)
            {
                newNotifications.Add(new InternalNotification
                {
                    RecipientUserName = userToNotify.UserName,
                    RecipientUserId = userToNotify.Id,
                    Type = request.Type,
                    Data = jsonData,
                    Text = request.Text,
                    ActionLink = request.ActionLink,
                    RelatedEntityType = request.RelatedEntity,
                    RelatedEntityId = request.RelatedEntityId,
                    SeenDate = null
                });
            }

            await _notificationRepository.BulkAddAsync(newNotifications, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Value.Error(ex, LogTemplate.Exception, ex.Message);
        }
    }

    /// <summary>
    /// Marks a single notification as seen.
    /// </summary>
    public Task MarkAsSeenAsync(List<long> notificationIdList, string currentUserName, CancellationToken cancellationToken)
    {
        if (notificationIdList.IsNullOrEmpty())
            return Task.CompletedTask;

        var now = DateTime.UtcNow;

        var propertyBuilder = new SetPropertyBuilder<InternalNotification>();

        propertyBuilder.SetPropertyValue(i => i.SeenDate, now);

        return _notificationRepository.ExecuteUpdateAsync(n => n.RecipientUserName == currentUserName && notificationIdList.Contains(n.Id), propertyBuilder, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Marks all of a user's notifications as seen.
    /// </summary>
    public Task MarkAllAsSeenAsync(string currentUserName, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var propertyBuilder = new SetPropertyBuilder<InternalNotification>();

        propertyBuilder.SetPropertyValue(i => i.SeenDate, now);

        return _notificationRepository.ExecuteUpdateAsync(n => n.RecipientUserName == currentUserName && !n.SeenDate.HasValue, propertyBuilder, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Deletes a list of notifications belonging to the current user.
    /// </summary>
    public Task DeleteAsync(IEnumerable<long> notificationIdList, string currentUserName, CancellationToken cancellationToken)
        => _notificationRepository.ExecuteDeleteAsync(n => n.RecipientUserName == currentUserName && notificationIdList.Contains(n.Id), cancellationToken: cancellationToken);

    /// <summary>
    /// Deletes all of notifications belonging to the current user.
    /// </summary>
    public Task DeleteAllAsync(string currentUserName, CancellationToken cancellationToken)
        => _notificationRepository.ExecuteDeleteAsync(n => n.RecipientUserName == currentUserName, cancellationToken: cancellationToken);
}