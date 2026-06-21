using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetWeekStartDayCommand(Guid UserId, int WeekStartDay) : IRequest<Result>, IConcurrencyRetryable;

public class SetWeekStartDayCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService) : IRequestHandler<SetWeekStartDayCommand, Result>
{
    public async Task<Result> Handle(SetWeekStartDayCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var result = user.SetWeekStartDay(request.WeekStartDay);

        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        userDateService.InvalidateUserDatePreferences(request.UserId);

        return Result.Success();
    }
}
