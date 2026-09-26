using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class QueryRoundTripCountTests
{
    private static readonly DateOnly DateFrom = new(2026, 6, 1);
    private static readonly DateOnly DateTo = new(2026, 6, 30);

    [Fact]
    public async Task DailySummaryRead_PreservesOverdueSkipsAndPriorBadHabitSlip()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var userId = Guid.NewGuid();
        var user = User.Create("Summary User", $"summary-{userId:N}@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        user.SetTimeZone("UTC");
        context.Users.Add(user);

        var overdue = Habit.Create(new HabitCreateParams(userId, "Overdue", FrequencyUnit.Day, 1,
            DueDate: today.AddDays(-3))).Value;
        var skippedToday = Habit.Create(new HabitCreateParams(userId, "Skipped today", FrequencyUnit.Day, 1,
            DueDate: today.AddDays(-3))).Value;
        var skippedEarlier = Habit.Create(new HabitCreateParams(userId, "Skipped earlier", FrequencyUnit.Day, 1,
            DueDate: today.AddDays(-3))).Value;
        var bad = Habit.Create(new HabitCreateParams(userId, "Bad habit", FrequencyUnit.Day, 1,
            DueDate: today.AddDays(-10), IsBadHabit: true)).Value;
        context.Habits.AddRange(overdue, skippedToday, skippedEarlier, bad);
        context.HabitLogs.AddRange(
            HabitLog.FromScheduleRead(Guid.NewGuid(), overdue.Id, today.AddDays(-3), 1, 0, DateTime.UtcNow),
            HabitLog.FromScheduleRead(Guid.NewGuid(), overdue.Id, today.AddDays(-2), 0, 0, DateTime.UtcNow),
            HabitLog.FromScheduleRead(Guid.NewGuid(), skippedToday.Id, today, 0, 0, DateTime.UtcNow),
            HabitLog.FromScheduleRead(Guid.NewGuid(), skippedEarlier.Id, today.AddDays(-1), 0, 0, DateTime.UtcNow),
            HabitLog.FromScheduleRead(Guid.NewGuid(), bad.Id, today.AddDays(-5), 1, 0, DateTime.UtcNow));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var payGate = Substitute.For<IPayGateService>();
        payGate.CanUseDailySummary(userId, Arg.Any<CancellationToken>()).Returns(Orbit.Domain.Common.Result.Success());
        var summaryService = Substitute.For<ISummaryService>();
        IReadOnlyList<Habit>? receivedHabits = null;
        DailySummaryContext? receivedContext = null;
        summaryService.GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                receivedHabits = call.ArgAt<IEnumerable<Habit>>(0).ToList();
                receivedContext = call.ArgAt<DailySummaryContext>(1);
                return Orbit.Domain.Common.Result.Success(new DailySummaryContent("Pinned summary", string.Empty));
            });
        var handler = new GetDailySummaryQueryHandler(
            new GenericRepository<Habit>(context),
            new GenericRepository<User>(context),
            new GenericRepository<HabitLog>(context),
            payGate, summaryService, new MemoryCache(new MemoryCacheOptions()));

        var result = await handler.Handle(new GetDailySummaryQuery(userId, today, today, "en"),
            CancellationToken.None);

        result.Value.Summary.Should().Be("Pinned summary");
        receivedHabits!.Select(habit => habit.Title).Should().BeEquivalentTo(
            ["Overdue", "Skipped earlier", "Bad habit"]);
        receivedHabits!.Single(habit => habit.Id == overdue.Id).Logs.Select(log => (log.Date, log.Value))
            .Should().Contain((today.AddDays(-2), 0));
        receivedHabits!.Single(habit => habit.Id == skippedEarlier.Id).Logs.Select(log => (log.Date, log.Value))
            .Should().Contain((today.AddDays(-1), 0));
        receivedContext!.LastBadHabitSlipDates[bad.Id].Should().Be(today.AddDays(-5));
    }

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

    [Fact]
    public async Task HabitDetailLoad_RoundTripCount_IsInvariantToChildVolume()
    {
        var small = await CountHabitDetailLoad(childCount: 2);
        var large = await CountHabitDetailLoad(childCount: 25);

        large.Should().Be(small);
        large.Should().BeLessThanOrEqualTo(7);
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

    private static async Task<int> CountHabitDetailLoad(int childCount)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var (userId, habitId) = await SeedDetailHabitGraph(factory.Context, childCount);
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(DateTo);
        var handler = new GetHabitFullDetailQueryHandler(
            new GenericRepository<Habit>(factory.Context),
            new GenericRepository<HabitLog>(factory.Context),
            new GenericRepository<User>(factory.Context),
            userDateService);

        counter.Reset();
        var result = await handler.Handle(
            new GetHabitFullDetailQuery(userId, habitId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habit.LinkedGoals.Should().HaveCount(2);
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

    private static async Task<(Guid UserId, Guid HabitId)> SeedDetailHabitGraph(
        OrbitDbContext context,
        int childCount)
    {
        var userId = Guid.NewGuid();
        var user = User.Create("Detail User", $"detail-{userId:N}@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        var parent = Habit.Create(new HabitCreateParams(
            userId, "Parent", FrequencyUnit.Day, 1, DueDate: DateFrom)).Value;
        var habit = Habit.Create(new HabitCreateParams(
            userId,
            "Nested Detail",
            FrequencyUnit.Day,
            1,
            DueDate: DateFrom,
            ParentHabitId: parent.Id,
            SlipAlertEnabled: true)).Value;

        context.Users.Add(user);
        context.Habits.AddRange(parent, habit);
        for (var goalIndex = 0; goalIndex < 2; goalIndex++)
        {
            var goal = Goal.Create(userId, $"Detail Goal {goalIndex}", 10m, "reps").Value;
            context.Goals.Add(goal);
            habit.AddGoal(goal);
        }

        for (var childIndex = 0; childIndex < childCount; childIndex++)
        {
            var child = Habit.Create(new HabitCreateParams(
                userId,
                $"Child {childIndex}",
                FrequencyUnit.Day,
                1,
                DueDate: DateFrom,
                ParentHabitId: habit.Id)).Value;
            var childGoal = Goal.Create(userId, $"Child Goal {childIndex}", 10m, "reps").Value;
            child.AddGoal(childGoal);
            context.Habits.Add(child);
            context.Goals.Add(childGoal);
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (userId, habit.Id);
    }
}
