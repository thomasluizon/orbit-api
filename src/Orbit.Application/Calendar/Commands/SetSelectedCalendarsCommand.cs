using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Commands;

public record SetSelectedCalendarsCommand(Guid UserId, IReadOnlyList<string> CalendarIds)
    : IRequest<Result>, IConcurrencyRetryable;

public class SetSelectedCalendarsCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetSelectedCalendarsCommand, Result>
{
    public async Task<Result> Handle(SetSelectedCalendarsCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.SetSelectedCalendars(request.CalendarIds);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
