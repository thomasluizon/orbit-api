using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record MarkNotificationReadCommand(Guid UserId, Guid NotificationId) : IRequest<Result>;

public class MarkNotificationReadCommandHandler(
    IGenericRepository<Notification> notificationRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<MarkNotificationReadCommand, Result>
{
    public async Task<Result> Handle(MarkNotificationReadCommand request, CancellationToken cancellationToken)
    {
        var notification = await notificationRepository.FindOneTrackedAsync(
            n => n.Id == request.NotificationId && n.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (notification is null)
            return Result.Failure(ErrorMessages.NotificationNotFound);

        notification.MarkAsRead();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
