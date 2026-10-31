using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

internal static class HabitSearchQuery
{
    internal static Task<IReadOnlyList<Habit>> ReadPageAsync(
        IGenericRepository<Habit> repository,
        Guid userId,
        IReadOnlyCollection<Guid> candidateIds,
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        repository.FindAsync(
            habit => habit.UserId == userId && candidateIds.Contains(habit.Id),
            query => query
                .OrderBy(habit => habit.Schedule == HabitSchedule.OneTime && habit.IsCompleted)
                .ThenBy(habit => habit.Schedule == HabitSchedule.OneTime && habit.IsCompleted
                    && !habit.Logs.Any(log => !log.IsDeleted && log.Value > 0))
                .ThenByDescending(habit => habit.Schedule == HabitSchedule.OneTime && habit.IsCompleted
                    ? habit.Logs.Where(log => !log.IsDeleted && log.Value > 0)
                        .Max(log => (DateOnly?)log.Date)
                    : null)
                .ThenBy(habit => habit.Position ?? int.MaxValue)
                .ThenBy(habit => habit.CreatedAtUtc)
                .ThenBy(habit => habit.Id)
                .Skip(offset)
                .Take(limit),
            cancellationToken);
}
