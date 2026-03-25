using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Queries;

public record HabitDetailResponse(
    Guid Id,
    string Title,
    string? Description,
    Domain.Enums.FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit,
    bool IsCompleted,
    bool IsGeneral,
    DateOnly DueDate,
    TimeOnly? DueTime,
    TimeOnly? DueEndTime,
    IReadOnlyList<DayOfWeek> Days,
    int? Position,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    DateTime CreatedAtUtc,
    IReadOnlyList<HabitChildResponse> Children);

public record GetHabitByIdQuery(Guid UserId, Guid HabitId) : IRequest<Result<HabitDetailResponse>>;

public class GetHabitByIdQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitByIdQuery, Result<HabitDetailResponse>>
{
    public async Task<Result<HabitDetailResponse>> Handle(GetHabitByIdQuery request, CancellationToken cancellationToken)
    {
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            cancellationToken);

        var habit = allHabits.FirstOrDefault(h => h.Id == request.HabitId);
        if (habit is null)
            return Result.Failure<HabitDetailResponse>(ErrorMessages.HabitNotFound);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);

        var children = lookup[habit.Id]
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChild(c, lookup))
            .ToList();

        return Result.Success(new HabitDetailResponse(
            habit.Id,
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.IsBadHabit,
            habit.IsCompleted,
            habit.IsGeneral,
            habit.DueDate,
            habit.DueTime,
            habit.DueEndTime,
            habit.Days.ToList(),
            habit.Position,
            habit.ChecklistItems,
            habit.CreatedAtUtc,
            children));
    }

    private static HabitChildResponse MapChild(Habit c, ILookup<Guid?, Habit> lookup) => new(
        c.Id, c.Title, c.Description,
        c.FrequencyUnit, c.FrequencyQuantity, c.IsBadHabit, c.IsCompleted, c.IsGeneral,
        c.Days.ToList(), c.DueDate, c.DueTime, c.DueEndTime,
        c.Position, c.ChecklistItems, MapChildren(c.Id, lookup));

    private static List<HabitChildResponse> MapChildren(Guid parentId, ILookup<Guid?, Habit> lookup) =>
        lookup[parentId]
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChild(c, lookup))
            .ToList();
}
