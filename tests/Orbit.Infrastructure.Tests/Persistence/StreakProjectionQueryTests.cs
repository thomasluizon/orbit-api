using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Gamification;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class StreakProjectionQueryTests
{
    [Fact]
    public async Task AchievementProgress_ReadsOnlyGoodHabitStreakEvidence()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Progress User", "progress@example.com").Value;
        var good = CreateHabit(user.Id, today.AddDays(-2));
        good.Log(today.AddDays(-1), advanceDueDate: false);
        good.Log(today.AddDays(-1), advanceDueDate: false);
        good.Log(today, advanceDueDate: false);
        good.Log(today.AddDays(-2), advanceDueDate: false);
        good.Unlog(today.AddDays(-2));
        var bad = Habit.Create(new HabitCreateParams(
            user.Id, "Bad", FrequencyUnit.Day, 1, today.AddDays(-2), IsBadHabit: true)).Value;
        bad.Log(today.AddDays(-1), advanceDueDate: false);
        bad.Log(today, advanceDueDate: false);
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(good, bad);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();
        var userDates = Substitute.For<IUserDateService>();
        userDates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var service = new AchievementProgressService(
            new GenericRepository<Habit>(factory.Context),
            new GenericRepository<HabitLog>(factory.Context),
            new GenericRepository<Goal>(factory.Context),
            userDates);

        counter.Reset();
        var metrics = await service.LoadAsync(user,
            new HashSet<string> { AchievementDefinitions.EarlyBird, AchievementDefinitions.NightOwl },
            CancellationToken.None);

        metrics.CurrentStreak.Should().Be(2);
        metrics.TotalCompletions.Should().Be(4);
        var logRead = counter.Commands.Single(command =>
            command.Sql.Contains("HabitLogs", StringComparison.Ordinal)
            && command.Sql.Split("FROM", 2)[0].Contains("\"Value\"", StringComparison.Ordinal));
        logRead.Rows.Should().Be(2);
        logRead.Sql.Split("FROM", 2)[0].Should().Contain("\"HabitId\"")
            .And.Contain("\"Date\"").And.Contain("\"Value\"")
            .And.NotContain("\"IsDeleted\"").And.NotContain("\"Note\"");
    }

    [Fact]
    public async Task StreakHistory_ProjectsDistinctCompletionDatesInSql()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("History User", "history@example.com").Value;
        user.SetStripeSubscription("sub", DateTime.UtcNow.AddYears(1));
        var first = CreateHabit(user.Id, today.AddDays(-2));
        var second = CreateHabit(user.Id, today.AddDays(-2));
        foreach (var date in new[] { today.AddDays(-2), today.AddDays(-1), today })
            first.Log(date, advanceDueDate: false);
        second.Log(today.AddDays(-1), advanceDueDate: false);
        second.Log(today, advanceDueDate: false);
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(first, second);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();
        var featureFlags = Substitute.For<IFeatureFlagService>();
        featureFlags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        var handler = new GetStreakHistoryQueryHandler(
            new GenericRepository<User>(factory.Context),
            new GenericRepository<Habit>(factory.Context),
            new GenericRepository<HabitLog>(factory.Context),
            new GenericRepository<StreakFreeze>(factory.Context),
            featureFlags);

        counter.Reset();
        var result = await handler.Handle(
            new GetStreakHistoryQuery(user.Id, today.AddDays(-2), today), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Points.Select(point => point.Streak).Should().Equal(1, 2, 3);
        var logRead = counter.Commands.Single(command => command.Sql.Contains("HabitLogs", StringComparison.Ordinal));
        logRead.Sql.Should().Contain("SELECT DISTINCT");
        logRead.Sql.Split("FROM", 2)[0].Should().Contain("\"Date\"")
            .And.NotContain("\"Note\"").And.NotContain("\"HabitId\"");
        logRead.Rows.Should().Be(3);
    }

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
