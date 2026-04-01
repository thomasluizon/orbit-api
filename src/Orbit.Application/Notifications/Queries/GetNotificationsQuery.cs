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
    int UnreadCount);

public record GetNotificationsQuery(Guid UserId) : IRequest<Result<GetNotificationsResponse>>;

public class GetNotificationsQueryHandler(
    IGenericRepository<Notification> notificationRepository) : IRequestHandler<GetNotificationsQuery, Result<GetNotificationsResponse>>
{
    public async Task<Result<GetNotificationsResponse>> Handle(GetNotificationsQuery request, CancellationToken cancellationToken)
    {
        var notifications = await notificationRepository.FindAsync(
            n => n.UserId == request.UserId,
            cancellationToken);

        var items = notifications
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(AppConstants.MaxNotificationsReturned)
            .Select(n => new NotificationItemDto(n.Id, n.Title, n.Body, n.Url, n.HabitId, n.IsRead, n.CreatedAtUtc))
            .ToList();

        var unreadCount = notifications.Count(n => !n.IsRead);

        return Result.Success(new GetNotificationsResponse(items, unreadCount));
    }
}
