using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Commands;

public record SetCalendarAutoSyncCommand(Guid UserId, bool Enabled) : IRequest<Result>;

public class SetCalendarAutoSyncCommandHandler(
    IGenericRepository<User> userRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<SetCalendarAutoSyncCommand, Result>
{
    public async Task<Result> Handle(SetCalendarAutoSyncCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageCalendar(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        if (request.Enabled)
        {
            var enableResult = user.EnableCalendarAutoSync();
            if (enableResult.IsFailure)
                return enableResult;
        }
        else
        {
            user.DisableCalendarAutoSync();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
