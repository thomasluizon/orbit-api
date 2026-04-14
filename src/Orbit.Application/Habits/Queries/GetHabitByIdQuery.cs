using MediatR;
using Microsoft.EntityFrameworkCore;
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
    string? Icon,
    Domain.Enums.FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit,
    bool IsCompleted,
    bool IsGeneral,
    bool IsFlexible,
    DateOnly DueDate,
    TimeOnly? DueTime,
    TimeOnly? DueEndTime,
    DateOnly? EndDate,
    IReadOnlyList<DayOfWeek> Days,
    int? Position,
    bool ReminderEnabled,
    IReadOnlyList<int> ReminderTimes,
    IReadOnlyList<ScheduledReminderTime> ScheduledReminders,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    DateTime CreatedAtUtc,
    IReadOnlyList<HabitChildResponse> Children);

public record GetHabitByIdQuery(Guid UserId, Guid HabitId) : IRequest<Result<HabitDetailResponse>>;

public class GetHabitByIdQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitByIdQuery, Result<HabitDetailResponse>>
{
    public async Task<Result<HabitDetailResponse>> Handle(GetHabitByIdQuery request, CancellationToken cancellationToken)
    {
        // Load the specific habit directly by ID + UserId (no need to load all user habits)
        var habits = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Children).ThenInclude(c => c.Children),
            cancellationToken);

        var habit = habits.Count > 0 ? habits[0] : null;
        if (habit is null)
            return Result.Failure<HabitDetailResponse>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        var children = habit.Children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChild(c))
            .ToList();

        return Result.Success(new HabitDetailResponse(
            habit.Id,
            habit.Title,
            habit.Description,
            habit.Icon,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.IsBadHabit,
            habit.IsCompleted,
            habit.IsGeneral,
            habit.IsFlexible,
            habit.DueDate,
            habit.DueTime,
            habit.DueEndTime,
            habit.EndDate,
            habit.Days.ToList(),
            habit.Position,
            habit.ReminderEnabled,
            habit.ReminderTimes,
            habit.ScheduledReminders,
            habit.ChecklistItems,
            habit.CreatedAtUtc,
            children));
    }

    private static HabitChildResponse MapChild(Habit c) => new(
        c.Id, c.Title, c.Description, c.Icon,
        c.FrequencyUnit, c.FrequencyQuantity, c.IsBadHabit, c.IsCompleted, c.IsGeneral, c.IsFlexible,
        c.Days.ToList(), c.DueDate, c.DueTime, c.DueEndTime, c.EndDate,
        c.Position, c.ChecklistItems, MapChildren(c));

    private static List<HabitChildResponse> MapChildren(Habit parent) =>
        parent.Children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChild(c))
            .ToList();
}
