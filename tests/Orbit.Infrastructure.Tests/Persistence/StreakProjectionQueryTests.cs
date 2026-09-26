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
    public async Task ScheduleProjection_KeepsScheduledDatesWithoutLoadingUnusedHabitColumns()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var start = new DateOnly(2026, 4, 1);
        var user = User.Create("Schedule User", "schedule@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Weekdays", FrequencyUnit.Day, 1,
            start, Days: [DayOfWeek.Monday, DayOfWeek.Wednesday])).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();

        var projectionQuery = HabitScheduleProjection.Select(
            factory.Context.Habits.AsNoTracking().Where(candidate => candidate.UserId == user.Id));
        var sql = projectionQuery.ToQueryString();
        var snapshot = await projectionQuery.SingleAsync();
        var projectedHabit = Habit.FromScheduleSnapshot(snapshot);
        var end = start.AddDays(14);

        HabitScheduleService.GetUnionScheduledDatesForStreak([projectedHabit], start, end, TimeZoneInfo.Utc)
            .Should().BeEquivalentTo(HabitScheduleService.GetUnionScheduledDatesForStreak([habit], start, end, TimeZoneInfo.Utc));
        sql.Should().NotContain("\"Description\"").And.NotContain("\"Emoji\"");
    }

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
        var projectedHabits = await habitRepository.ProjectAsync(
            habit => habit.UserId == user.Id, HabitScheduleProjection.Select);
        var projectedAchievementRows = await logRepository.ProjectAsync(
            log => habitIds.Contains(log.HabitId) && log.Date >= today.AddDays(-1100),
            query => query.Select(log => new HabitMetricLog(log.HabitId, log.Date, log.Value, log.IsDeleted)));

        fullStreakRows.Should().HaveCount(5);
        streakDates.Should().HaveCount(3);
        fullAchievementHabits.Sum(habit => habit.Logs.Count).Should().Be(5);
        projectedHabits.Should().HaveCount(2);
        projectedAchievementRows.Should().HaveCount(5);
        foreach (var habit in fullAchievementHabits)
        {
            var projectedHabit = Habit.FromScheduleSnapshot(projectedHabits.Single(snapshot => snapshot.Id == habit.Id));
            HabitMetricsCalculator.CalculateProjected(projectedHabit,
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
