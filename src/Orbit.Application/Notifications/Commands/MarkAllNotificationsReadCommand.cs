using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record MarkAllNotificationsReadCommand(Guid UserId) : IRequest<Result>;

public class MarkAllNotificationsReadCommandHandler(
    IGenericRepository<Notification> notificationRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<MarkAllNotificationsReadCommand, Result>
{
    public async Task<Result> Handle(MarkAllNotificationsReadCommand request, CancellationToken cancellationToken)
    {
        var unreadNotifications = await notificationRepository.FindTrackedAsync(
            n => n.UserId == request.UserId && !n.IsRead,
            cancellationToken);

        foreach (var notification in unreadNotifications)
            notification.MarkAsRead();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
