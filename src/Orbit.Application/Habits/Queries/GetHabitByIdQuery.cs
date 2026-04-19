using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
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
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IRequestHandler<GetHabitByIdQuery, Result<HabitDetailResponse>>
{
    public async Task<Result<HabitDetailResponse>> Handle(GetHabitByIdQuery request, CancellationToken cancellationToken)
    {
        // Load the specific habit directly by ID + UserId (no need to load all user habits)
        var habits = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Children).ThenInclude(c => c.Children)
                  .Include(h => h.Children).ThenInclude(c => c.Logs)
                  .Include(h => h.Children).ThenInclude(c => c.Children).ThenInclude(gc => gc.Logs),
            cancellationToken);

        var habit = habits.Count > 0 ? habits[0] : null;
        if (habit is null)
            return Result.Failure<HabitDetailResponse>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var children = HabitDetailChildMapper.MapChildren(habit, userToday);

        return Result.Success(new HabitDetailResponse(
            habit.Id,
            habit.Title,
            habit.Description,
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
}

internal static class HabitDetailChildMapper
{
    public static List<HabitChildResponse> MapChildren(Habit parent, DateOnly userToday) =>
        parent.Children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChild(c, userToday))
            .ToList();

    private static HabitChildResponse MapChild(Habit child, DateOnly userToday) => new(
        child.Id,
        child.Title,
        child.Description,
        child.FrequencyUnit,
        child.FrequencyQuantity,
        child.IsBadHabit,
        child.IsCompleted,
        child.IsGeneral,
        child.IsFlexible,
        child.Days.ToList(),
        child.DueDate,
        child.DueTime,
        child.DueEndTime,
        child.EndDate,
        child.Position,
        child.ChecklistItems,
        DetermineOverdueStatus(child, userToday),
        MapChildren(child, userToday));

    private static bool DetermineOverdueStatus(Habit habit, DateOnly userToday)
    {
        if (habit.IsCompleted || habit.IsFlexible || habit.IsBadHabit)
            return false;

        if (habit.FrequencyUnit == null)
        {
            return habit.DueDate < userToday
                && (!habit.EndDate.HasValue || habit.EndDate.Value >= userToday);
        }

        if (HabitScheduleService.GetScheduledDates(habit, userToday, userToday).Contains(userToday))
            return false;

        return HasMissedRecentOccurrence(habit, userToday);
    }

    private static bool HasMissedRecentOccurrence(Habit habit, DateOnly userToday)
    {
        var qty = habit.FrequencyQuantity ?? 1;
        var lookbackDays = HabitScheduleService.GetLookbackDays(habit.FrequencyUnit, qty);
        var lookbackStart = userToday.AddDays(-lookbackDays);

        if (habit.DueDate > lookbackStart)
            lookbackStart = habit.DueDate;

        var pastDates = HabitScheduleService.GetScheduledDates(habit, lookbackStart, userToday.AddDays(-1));
        var logDates = habit.Logs.Select(l => l.Date).ToHashSet();

        return pastDates.Any(d => !logDates.Contains(d));
    }
}
