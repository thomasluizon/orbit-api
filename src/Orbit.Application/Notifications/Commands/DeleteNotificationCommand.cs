using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record DeleteNotificationCommand(Guid UserId, Guid NotificationId) : IRequest<Result>;

public class DeleteNotificationCommandHandler(
    IGenericRepository<Notification> notificationRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DeleteNotificationCommand, Result>
{
    public async Task<Result> Handle(DeleteNotificationCommand request, CancellationToken cancellationToken)
    {
        var notification = await notificationRepository.FindOneTrackedAsync(
            n => n.Id == request.NotificationId && n.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (notification is not null)
        {
            notificationRepository.Remove(notification);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
