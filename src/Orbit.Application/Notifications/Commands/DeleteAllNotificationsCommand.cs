using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record DeleteAllNotificationsCommand(Guid UserId) : IRequest<Result>;

public class DeleteAllNotificationsCommandHandler(
    IGenericRepository<Notification> notificationRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DeleteAllNotificationsCommand, Result>
{
    public async Task<Result> Handle(DeleteAllNotificationsCommand request, CancellationToken cancellationToken)
    {
        var notifications = await notificationRepository.FindTrackedAsync(
            n => n.UserId == request.UserId,
            cancellationToken);

        notificationRepository.RemoveRange(notifications);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
