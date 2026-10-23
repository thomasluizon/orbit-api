using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetTimezoneCommand(Guid UserId, string TimeZone) : IRequest<Result>, IConcurrencyRetryable;

public class SetTimezoneCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService) : IRequestHandler<SetTimezoneCommand, Result>
{
    public async Task<Result> Handle(SetTimezoneCommand request, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync(
            ct => SetTimezoneAsync(request, ct), cancellationToken);
        if (result.IsSuccess)
            await userDateService.InvalidateUserDatePreferencesAsync(request.UserId, cancellationToken);
        return result;
    }

    private async Task<Result> SetTimezoneAsync(SetTimezoneCommand request, CancellationToken cancellationToken)
    {
        await unitOfWork.AcquireUserRowLockAsync(request.UserId, cancellationToken);
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        await userRepository.ReloadAsync(user, cancellationToken);
        var result = user.SetTimeZone(request.TimeZone);

        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
