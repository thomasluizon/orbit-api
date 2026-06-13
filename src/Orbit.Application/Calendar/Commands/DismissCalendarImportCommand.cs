using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Commands;

public record DismissCalendarImportCommand(Guid UserId) : IRequest<Result>;

/// <summary>
/// Suppresses the calendar-import prompt for the user. Intentionally NOT pay-gated:
/// the prompt is shown to every onboarded user, so every user must be able to
/// dismiss it permanently — gating this behind Pro made the modal resurrect on
/// every app restart for free accounts.
/// </summary>
public class DismissCalendarImportCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DismissCalendarImportCommand, Result>
{
    public async Task<Result> Handle(DismissCalendarImportCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.MarkCalendarImported();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
