using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record HabitResponse(
    Guid Id,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit,
    bool IsCompleted,
    DateOnly DueDate,
    IReadOnlyList<DayOfWeek> Days,
    int? Position,
    DateTime CreatedAtUtc,
    IReadOnlyList<HabitChildResponse> Children,
    IReadOnlyList<TagResponse> Tags);

public record HabitChildResponse(
    Guid Id,
    string Title,
    string? Description,
    bool IsCompleted,
    DateOnly DueDate,
    int? Position,
    IReadOnlyList<HabitChildResponse> Children);

public record TagResponse(
    Guid Id,
    string Name,
    string Color);

public record GetHabitsQuery(Guid UserId, IReadOnlyList<Guid>? TagIds = null) : IRequest<IReadOnlyList<HabitResponse>>;

public class GetHabitsQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitsQuery, IReadOnlyList<HabitResponse>>
{
    public async Task<IReadOnlyList<HabitResponse>> Handle(GetHabitsQuery request, CancellationToken cancellationToken)
    {
        // Load all active habits for the user in one query to build the tree in-memory
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsActive,
            q => q.Include(h => h.Tags),
            cancellationToken);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);

        var topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc);

        if (request.TagIds is { Count: > 0 })
        {
            return topLevel
                .Where(h => h.Tags.Any(t => request.TagIds.Contains(t.Id)))
                .Select(h => MapToResponse(h, lookup))
                .ToList();
        }

        return topLevel
            .Select(h => MapToResponse(h, lookup))
            .ToList();
    }

    private static HabitResponse MapToResponse(Habit h, ILookup<Guid?, Habit> lookup) => new(
        h.Id,
        h.Title,
        h.Description,
        h.FrequencyUnit,
        h.FrequencyQuantity,
        h.IsBadHabit,
        h.IsCompleted,
        h.DueDate,
        h.Days.ToList(),
        h.Position,
        h.CreatedAtUtc,
        MapChildren(h.Id, lookup),
        h.Tags.Select(t => new TagResponse(t.Id, t.Name, t.Color)).ToList());

    private static List<HabitChildResponse> MapChildren(Guid parentId, ILookup<Guid?, Habit> lookup) =>
        lookup[parentId]
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => new HabitChildResponse(
                c.Id, c.Title, c.Description, c.IsCompleted, c.DueDate,
                c.Position, MapChildren(c.Id, lookup)))
            .ToList();
}
