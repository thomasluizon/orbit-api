using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetTimezoneCommand(Guid UserId, string TimeZone) : IRequest<Result>;

public class SetTimezoneCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService) : IRequestHandler<SetTimezoneCommand, Result>
{
    public async Task<Result> Handle(SetTimezoneCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var result = user.SetTimeZone(request.TimeZone);

        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        userDateService.InvalidateUserTimezone(request.UserId);

        return Result.Success();
    }
}
