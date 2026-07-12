using MediatR;
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
    IGenericRepository<GoalProgressLog> progressLogRepository,
    IPayGateService payGate) : IRequestHandler<GetGoalProgressHistoryQuery, Result<GoalProgressHistoryResponse>>
{
    public async Task<Result<GoalProgressHistoryResponse>> Handle(GetGoalProgressHistoryQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<GoalProgressHistoryResponse>();

        var goalExists = await goalRepository.AnyAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            cancellationToken);
        if (!goalExists)
            return Result.Failure<GoalProgressHistoryResponse>(ErrorMessages.GoalNotFound);

        var fromUtc = request.DateFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toExclusiveUtc = request.DateTo.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var logs = await progressLogRepository.FindAsync(
            l => l.GoalId == request.GoalId && l.CreatedAtUtc >= fromUtc && l.CreatedAtUtc < toExclusiveUtc,
            cancellationToken);

        var points = logs
            .Select(l => new GoalProgressHistoryPoint(
                DateOnly.FromDateTime(l.CreatedAtUtc), l.Value, l.PreviousValue, l.Note))
            .OrderBy(p => p.Date)
            .ToList();

        return Result.Success(new GoalProgressHistoryResponse(request.GoalId, points));
    }
}
