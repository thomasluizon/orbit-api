using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class StreakProjectionQueryTests
{
    [Fact]
    public async Task SameSeed_ProjectsDistinctStreakDatesAndPreservesAchievementMetrics()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Projection User", "projection@example.com").Value;
        var first = CreateHabit(user.Id, today.AddDays(-2));
        var second = CreateHabit(user.Id, today.AddDays(-2));
        first.Log(today.AddDays(-2), advanceDueDate: false);
        first.Log(today.AddDays(-1), advanceDueDate: false);
        first.Log(today, advanceDueDate: false);
        second.Log(today.AddDays(-1), advanceDueDate: false);
        second.Log(today, advanceDueDate: false);
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(first, second);
        await factory.Context.SaveChangesAsync();

        var habitIds = new[] { first.Id, second.Id };
        var logRepository = new GenericRepository<HabitLog>(factory.Context);
        var habitRepository = new GenericRepository<Habit>(factory.Context);
        var fullStreakRows = await logRepository.FindAsync(
            log => habitIds.Contains(log.HabitId) && log.Value > 0 && log.Date >= today.AddDays(-1100));
        var streakDates = await logRepository.ProjectAsync(
            log => habitIds.Contains(log.HabitId) && log.Value > 0 && log.Date >= today.AddDays(-1100),
            query => query.Select(log => log.Date).Distinct());
        var fullAchievementHabits = await habitRepository.FindAsync(
            habit => habit.UserId == user.Id,
            query => query.Include(habit => habit.Logs.Where(log => log.Date >= today.AddDays(-1100))));
        var projectedAchievementRows = await logRepository.ProjectAsync(
            log => habitIds.Contains(log.HabitId) && log.Date >= today.AddDays(-1100),
            query => query.Select(log => new HabitMetricLog(log.HabitId, log.Date, log.Value, log.IsDeleted)));

        fullStreakRows.Should().HaveCount(5);
        streakDates.Should().HaveCount(3);
        fullAchievementHabits.Sum(habit => habit.Logs.Count).Should().Be(5);
        projectedAchievementRows.Should().HaveCount(5);
        foreach (var habit in fullAchievementHabits)
        {
            HabitMetricsCalculator.CalculateProjected(habit,
                projectedAchievementRows.Where(log => log.HabitId == habit.Id).ToList(), today, 1)
                .Should().Be(HabitMetricsCalculator.Calculate(habit, today, 1));
        }
    }

    private static Habit CreateHabit(Guid userId, DateOnly start)
    {
        var habit = Habit.Create(new HabitCreateParams(userId, "Daily", FrequencyUnit.Day, 1, start)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        return habit;
    }
}
