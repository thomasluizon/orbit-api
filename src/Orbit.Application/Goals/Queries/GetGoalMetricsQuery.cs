using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Goals.Queries;

public record GetGoalMetricsQuery(Guid UserId, Guid GoalId) : IRequest<Result<GoalMetrics>>;

public class GetGoalMetricsQueryHandler(
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService) : IRequestHandler<GetGoalMetricsQuery, Result<GoalMetrics>>
{
    public async Task<Result<GoalMetrics>> Handle(GetGoalMetricsQuery request, CancellationToken cancellationToken)
    {
        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            q => q.Include(g => g.ProgressLogs)
                  .Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken);

        if (goal is null)
            return Result.Failure<GoalMetrics>(ErrorMessages.GoalNotFound, ErrorCodes.GoalNotFound);

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var metrics = GoalMetricsCalculator.Calculate(goal, userToday);
        return Result.Success(metrics);
    }
}
