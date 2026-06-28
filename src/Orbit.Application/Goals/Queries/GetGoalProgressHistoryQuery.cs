using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Queries;

public record GoalProgressHistoryPoint(DateOnly Date, decimal Value, decimal PreviousValue, string? Note);

public record GoalProgressHistoryResponse(Guid GoalId, IReadOnlyList<GoalProgressHistoryPoint> Points);

public record GetGoalProgressHistoryQuery(
    Guid UserId,
    Guid GoalId,
    DateOnly DateFrom,
    DateOnly DateTo) : IRequest<Result<GoalProgressHistoryResponse>>;

/// <summary>
/// Returns a goal's progress-log entries within a date range as an ascending series, for charting
/// progress over time. Pro-gated behind goals access; scoped to the requesting user's own goal.
/// </summary>
public class GetGoalProgressHistoryQueryHandler(
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate) : IRequestHandler<GetGoalProgressHistoryQuery, Result<GoalProgressHistoryResponse>>
{
    public async Task<Result<GoalProgressHistoryResponse>> Handle(GetGoalProgressHistoryQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<GoalProgressHistoryResponse>();

        var goals = await goalRepository.FindAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            q => q.Include(g => g.ProgressLogs),
            cancellationToken);
        var goal = goals.FirstOrDefault();
        if (goal is null)
            return Result.Failure<GoalProgressHistoryResponse>(ErrorMessages.GoalNotFound);

        var points = goal.ProgressLogs
            .Select(l => new GoalProgressHistoryPoint(
                DateOnly.FromDateTime(l.CreatedAtUtc), l.Value, l.PreviousValue, l.Note))
            .Where(p => p.Date >= request.DateFrom && p.Date <= request.DateTo)
            .OrderBy(p => p.Date)
            .ToList();

        return Result.Success(new GoalProgressHistoryResponse(goal.Id, points));
    }
}
