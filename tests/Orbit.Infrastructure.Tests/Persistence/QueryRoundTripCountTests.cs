using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Guards the filtered-include and batched read paths against N+1 regressions by asserting their SQL
/// round-trip count is invariant to how many rows are seeded. Runs on SQLite (real SQL, counted by
/// <see cref="CountingDbCommandInterceptor"/>) rather than a per-row check, so a future edit that turns
/// a filtered <c>Include</c> or an <c>IN</c>-list batch load into a per-habit query fails the invariance
/// assertion. Mirrors the exact include shapes of <c>GetRetrospectiveQuery</c>,
/// <c>GetDailySummaryQuery</c>, and <c>ExportUserDataQuery</c>.
/// </summary>
public class QueryRoundTripCountTests
{
    private static readonly DateOnly DateFrom = new(2026, 6, 1);
    private static readonly DateOnly DateTo = new(2026, 6, 30);

    [Fact]
    public async Task RetrospectiveHabitLoad_RoundTripCount_IsInvariantToHabitVolume()
    {
        var small = await CountRetrospectiveLoad(habitCount: 2);
        var large = await CountRetrospectiveLoad(habitCount: 25);

        large.Should().Be(small);
        large.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task DailySummaryHabitLoad_RoundTripCount_IsInvariantToHabitVolume()
    {
        var small = await CountDailySummaryLoad(habitCount: 2);
        var large = await CountDailySummaryLoad(habitCount: 25);

        large.Should().Be(small);
        large.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task ExportHabitLogLoad_IsOneBatchedRoundTrip_NotPerHabit()
    {
        var small = await CountExportHabitLogLoad(habitCount: 2);
        var large = await CountExportHabitLogLoad(habitCount: 25);

        large.Should().Be(small);
        large.Should().Be(1);
    }

    private static async Task<int> CountRetrospectiveLoad(int habitCount)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var userId = await SeedHabits(factory.Context, habitCount, logsPerHabit: 8, withGoals: false);

        counter.Reset();
        await new GenericRepository<Habit>(factory.Context).FindAsync(
            habit => habit.UserId == userId,
            query => query.Include(habit => habit.Logs.Where(log => log.Date >= DateFrom && log.Date <= DateTo)),
            CancellationToken.None);
        return counter.CommandCount;
    }

    private static async Task<int> CountDailySummaryLoad(int habitCount)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var userId = await SeedHabits(factory.Context, habitCount, logsPerHabit: 8, withGoals: true);

        counter.Reset();
        await new GenericRepository<Habit>(factory.Context).FindAsync(
            habit => habit.UserId == userId && !habit.IsGeneral,
            query => query
                .Include(habit => habit.Logs.Where(log => log.Date >= DateFrom && log.Date <= DateTo))
                .Include(habit => habit.Goals),
            CancellationToken.None);
        return counter.CommandCount;
    }

    private static async Task<int> CountExportHabitLogLoad(int habitCount)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var userId = await SeedHabits(factory.Context, habitCount, logsPerHabit: 8, withGoals: false);

        var habits = await new GenericRepository<Habit>(factory.Context).FindAsync(
            habit => habit.UserId == userId, CancellationToken.None);
        var habitIds = habits.Select(habit => habit.Id).ToHashSet();

        counter.Reset();
        await new GenericRepository<HabitLog>(factory.Context).FindAsync(
            log => habitIds.Contains(log.HabitId), CancellationToken.None);
        return counter.CommandCount;
    }

    private static async Task<Guid> SeedHabits(OrbitDbContext context, int habitCount, int logsPerHabit, bool withGoals)
    {
        var userId = Guid.NewGuid();
        var user = User.Create("Bench User", $"bench-{userId:N}@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        context.Users.Add(user);

        for (var index = 0; index < habitCount; index++)
        {
            var habit = Habit.Create(new HabitCreateParams(
                userId, $"Habit {index}", FrequencyUnit.Day, 1, DueDate: DateFrom)).Value;

            for (var offset = 0; offset < logsPerHabit; offset++)
                habit.Log(DateFrom.AddDays(offset), advanceDueDate: false);

            if (withGoals)
            {
                var goal = Goal.Create(userId, $"Goal {index}", 10m, "reps").Value;
                context.Goals.Add(goal);
                habit.AddGoal(goal);
            }

            context.Habits.Add(habit);
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return userId;
    }
}
