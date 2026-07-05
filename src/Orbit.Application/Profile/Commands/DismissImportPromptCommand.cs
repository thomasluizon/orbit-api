using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record DismissImportPromptCommand(Guid UserId) : IRequest<Result>, IConcurrencyRetryable;

/// <summary>
/// Marks the one-time "import from another app?" prompt as seen for the user. Not pay-gated: the
/// prompt is shown to every account exactly once, so every account must be able to dismiss it
/// permanently regardless of plan.
/// </summary>
public class DismissImportPromptCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DismissImportPromptCommand, Result>
{
    public async Task<Result> Handle(DismissImportPromptCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.MarkImportPromptSeen();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
