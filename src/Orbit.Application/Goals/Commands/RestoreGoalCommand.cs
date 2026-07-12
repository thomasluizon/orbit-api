using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record RestoreGoalCommand(
    Guid UserId,
    Guid GoalId) : IRequest<Result>, IConcurrencyRetryable;

public class RestoreGoalCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<RestoreGoalCommand, Result>
{
    public async Task<Result> Handle(RestoreGoalCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var goals = await goalRepository.FindTrackedIgnoringFiltersAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            cancellationToken);

        var goal = goals.FirstOrDefault();
        if (goal is null || !goal.IsDeleted)
            return Result.Failure(ErrorMessages.GoalNotFound);

        goal.Restore();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId);

        return Result.Success();
    }
}
