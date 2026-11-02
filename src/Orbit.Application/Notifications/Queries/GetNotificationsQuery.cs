using System.Linq.Expressions;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Queries;

public record NotificationItemDto(
    Guid Id,
    string Title,
    string Body,
    string? Url,
    Guid? HabitId,
    bool IsRead,
    DateTime CreatedAtUtc);

public record GetNotificationsResponse(
    IReadOnlyList<NotificationItemDto> Items,
    int UnreadCount,
    int? TotalCount = null,
    string? SourceRevision = null);

public record GetNotificationsQuery(Guid UserId, int Offset = 0, int Limit = AppConstants.MaxNotificationsReturned) : IRequest<Result<GetNotificationsResponse>>;

public class GetNotificationsQueryHandler(
    IGenericRepository<Notification> notificationRepository, INotificationInboxAggregateReader aggregateReader) : IRequestHandler<GetNotificationsQuery, Result<GetNotificationsResponse>>
{
    public async Task<Result<GetNotificationsResponse>> Handle(GetNotificationsQuery request, CancellationToken cancellationToken)
    {
        var notifications = await notificationRepository.FindAsync(
            VisibleForUser(request.UserId),
            q => q.OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id)
                .Skip(request.Offset).Take(request.Limit),
            cancellationToken);

        var items = notifications
            .Select(n => new NotificationItemDto(n.Id, n.Title, n.Body, n.Url, n.HabitId, n.IsRead, n.CreatedAtUtc))
            .ToList();

        var aggregate = await aggregateReader.ReadAsync(request.UserId, cancellationToken);
        return Result.Success(new GetNotificationsResponse(items, aggregate.UnreadCount, aggregate.TotalCount, aggregate.Revision));
    }

    // Mirrors RETIRED_NOTIFICATION_URL_PREFIXES so counts match both inboxes: https://github.com/thomasluizon/orbit-ui-mobile/blob/main/packages/shared/src/utils/notification-actions.ts
    private static Expression<Func<Notification, bool>> VisibleForUser(Guid userId, bool unreadOnly = false) =>
        n => n.UserId == userId && (!unreadOnly || !n.IsRead)
            && (n.Url == null || (!n.Url.StartsWith("/social")
                && !n.Url.StartsWith("/public-profile")
                && !n.Url.StartsWith("/u/")));
}
