using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Services;

internal static class SkipHabitArgumentChecks
{
    public static Result Check(Habit habit, DateOnly targetDate, DateOnly today, int weekStartDay,
        bool allowOverdue, bool dueDateResolved, bool enforceWindow)
    {
        if (habit.IsCompleted)
            return Result.Failure(ErrorMessages.CannotSkipCompletedHabit);
        if (targetDate > today)
            return Result.Failure(ErrorMessages.CannotSkipFutureDate);
        if (enforceWindow && targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return Result.Failure(ErrorMessages.BeyondOverdueWindow);
        if (habit.FrequencyUnit is null)
            return Result.Success();
        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return Result.Failure(ErrorMessages.HabitNotYetDue);
        if (!HabitScheduleService.IsHabitDueOnDate(habit, targetDate, weekStartDay))
        {
            var isOverdue = allowOverdue && !habit.IsFlexible && targetDate == today
                && HabitScheduleService.HasMissedPastOccurrence(habit, today, weekStartDay, dueDateResolved);
            if (!isOverdue)
                return Result.Failure(ErrorMessages.NotScheduledOnDate);
        }
        if (habit.IsFlexible && HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs, weekStartDay) <= 0)
            return Result.Failure(ErrorMessages.AllInstancesDone);
        return Result.Success();
    }
}
