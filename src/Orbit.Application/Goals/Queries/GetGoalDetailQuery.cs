using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Goals.Queries;

public record GoalDetailWithMetricsResponse(
    GoalDetailDto Goal,
    GoalMetrics Metrics);

public record GetGoalDetailQuery(
    Guid UserId,
    Guid GoalId) : IRequest<Result<GoalDetailWithMetricsResponse>>;

public class GetGoalDetailQueryHandler(
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IUserDateService userDateService) : IRequestHandler<GetGoalDetailQuery, Result<GoalDetailWithMetricsResponse>>
{
    public async Task<Result<GoalDetailWithMetricsResponse>> Handle(GetGoalDetailQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<GoalDetailWithMetricsResponse>();

        var loaded = await GoalDetailLoader.BuildGoalDetailAsync(
            goalRepository, userDateService, request.GoalId, request.UserId, cancellationToken);
        if (loaded is null)
            return Result.Failure<GoalDetailWithMetricsResponse>(ErrorMessages.GoalNotFound);

        var goalMetrics = GoalMetricsCalculator.Calculate(loaded.Goal, loaded.UserToday);

        return Result.Success(new GoalDetailWithMetricsResponse(loaded.Dto, goalMetrics));
    }
}
