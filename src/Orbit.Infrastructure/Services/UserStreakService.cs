using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public class UserStreakService(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository) : IUserStreakService
{
    public async Task<UserStreakState?> RecalculateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);
        if (user is null)
            return null;

        var habits = await habitRepository.FindAsync(h => h.UserId == userId, cancellationToken);
        var habitIds = habits.Select(h => h.Id).ToHashSet();
        var completionDates = habitIds.Count == 0
            ? []
            : (await habitLogRepository.FindAsync(
                l => habitIds.Contains(l.HabitId) && l.Value > 0,
                cancellationToken))
                .Select(log => log.Date)
                .Distinct()
                .ToList();

        var freezeDates = (await streakFreezeRepository.FindAsync(
            sf => sf.UserId == userId,
            cancellationToken))
            .Select(freeze => freeze.UsedOnDate)
            .Distinct()
            .ToList();

        var completionDateSet = completionDates.ToHashSet();
        var freezeDateSet = freezeDates.ToHashSet();
        var orderedDates = completionDates
            .Concat(freezeDates)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        var currentStreak = 0;
        var longestStreak = 0;
        DateOnly? lastActiveDate = null;

        foreach (var date in orderedDates)
        {
            if (completionDateSet.Contains(date))
            {
                currentStreak = lastActiveDate == date.AddDays(-1)
                    ? currentStreak + 1
                    : 1;

                lastActiveDate = date;
                longestStreak = Math.Max(longestStreak, currentStreak);
                continue;
            }

            if (!freezeDateSet.Contains(date))
                continue;

            // A freeze preserves the streak if it is adjacent to the last active date
            // OR bridges exactly a 1-day gap (the missed day the freeze is meant to cover).
            if (lastActiveDate.HasValue
                && (date.DayNumber - lastActiveDate.Value.DayNumber) <= 2)
            {
                // Streak preserved -- do not increment
                lastActiveDate = date;
            }
            else
            {
                // Gap too large for a single freeze to bridge
                currentStreak = 0;
                lastActiveDate = date;
            }
        }

        user.SetStreakState(currentStreak, longestStreak, lastActiveDate);

        return new UserStreakState(currentStreak, longestStreak, lastActiveDate);
    }
}
