using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetClockFormatCommand(Guid UserId, bool Uses24HourClock) : IRequest<Result>, IConcurrencyRetryable;

public class SetClockFormatCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetClockFormatCommand, Result>
{
    public async Task<Result> Handle(SetClockFormatCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.SetClockFormat(request.Uses24HourClock);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
