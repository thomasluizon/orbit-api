using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Habits.Services;

internal static class HabitCompletionLogResolver
{
    public static HabitLog? Resolve(Habit habit, DateOnly date)
    {
        var completions = habit.Logs.Where(log => log.Value > 0 && !log.IsDeleted);
        return habit.Schedule == HabitSchedule.General && habit.IsCompleted
            ? completions.OrderByDescending(log => log.CreatedAtUtc)
                .ThenByDescending(log => log.CompletionOrdinal)
                .ThenByDescending(log => log.Id)
                .FirstOrDefault()
            : completions.FirstOrDefault(log => log.Date == date);
    }
}
