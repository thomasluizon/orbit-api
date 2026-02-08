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
    DateTime CreatedAtUtc,
    IReadOnlyList<HabitChildResponse> Children,
    IReadOnlyList<TagResponse> Tags);

public record HabitChildResponse(
    Guid Id,
    string Title,
    string? Description,
    bool IsCompleted,
    DateOnly DueDate);

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
        IReadOnlyList<Habit> habits;

        if (request.TagIds is { Count: > 0 })
        {
            habits = await habitRepository.FindAsync(
                h => h.UserId == request.UserId && h.IsActive && h.ParentHabitId == null
                     && h.Tags.Any(t => request.TagIds.Contains(t.Id)),
                q => q.Include(h => h.Children.Where(c => c.IsActive))
                      .Include(h => h.Tags),
                cancellationToken);
        }
        else
        {
            habits = await habitRepository.FindAsync(
                h => h.UserId == request.UserId && h.IsActive && h.ParentHabitId == null,
                q => q.Include(h => h.Children.Where(c => c.IsActive))
                      .Include(h => h.Tags),
                cancellationToken);
        }

        return habits.Select(MapToResponse).ToList();
    }

    private static HabitResponse MapToResponse(Habit h) => new(
        h.Id,
        h.Title,
        h.Description,
        h.FrequencyUnit,
        h.FrequencyQuantity,
        h.IsBadHabit,
        h.IsCompleted,
        h.DueDate,
        h.Days.ToList(),
        h.CreatedAtUtc,
        h.Children.Select(c => new HabitChildResponse(
            c.Id, c.Title, c.Description, c.IsCompleted, c.DueDate)).ToList(),
        h.Tags.Select(t => new TagResponse(t.Id, t.Name, t.Color)).ToList());
}
