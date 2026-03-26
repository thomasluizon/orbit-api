using MediatR;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Queries;

public record GoalDto(
    Guid Id,
    string Title,
    string? Description,
    decimal TargetValue,
    decimal CurrentValue,
    string Unit,
    GoalStatus Status,
    DateOnly? Deadline,
    int Position,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    decimal ProgressPercentage);

public record PaginatedGoalResult(
    IReadOnlyList<GoalDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public record GetGoalsQuery(
    Guid UserId,
    GoalStatus? StatusFilter = null,
    int Page = 1,
    int PageSize = 50) : IRequest<PaginatedGoalResult>;

public class GetGoalsQueryHandler(
    IGenericRepository<Goal> goalRepository) : IRequestHandler<GetGoalsQuery, PaginatedGoalResult>
{
    public async Task<PaginatedGoalResult> Handle(GetGoalsQuery request, CancellationToken cancellationToken)
    {
        var allGoals = await goalRepository.FindAsync(
            g => g.UserId == request.UserId,
            cancellationToken);

        IEnumerable<Goal> filtered = allGoals;

        if (request.StatusFilter.HasValue)
            filtered = filtered.Where(g => g.Status == request.StatusFilter.Value);

        var ordered = filtered
            .OrderBy(g => g.Position)
            .ThenBy(g => g.CreatedAtUtc)
            .ToList();

        var totalCount = ordered.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);

        var items = ordered
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(MapToDto)
            .ToList();

        return new PaginatedGoalResult(items, request.Page, request.PageSize, totalCount, totalPages);
    }

    private static GoalDto MapToDto(Goal g) => new(
        g.Id, g.Title, g.Description, g.TargetValue, g.CurrentValue,
        g.Unit, g.Status, g.Deadline, g.Position, g.CreatedAtUtc, g.CompletedAtUtc,
        g.TargetValue > 0 ? Math.Min(100, Math.Round(g.CurrentValue / g.TargetValue * 100, 1)) : 0);
}
