using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitLogReaderTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Anchor = new(2026, 7, 1);

    private static Habit RecurringHabit() =>
        Habit.Create(new HabitCreateParams(UserId, "H", FrequencyUnit.Day, 1, DueDate: Anchor)).Value;

    private static Habit FlexibleHabit() =>
        Habit.Create(new HabitCreateParams(UserId, "F", FrequencyUnit.Week, 3, DueDate: Anchor, IsFlexible: true)).Value;

    private static HabitLog Log(Habit habit, DateOnly date) => habit.Log(date, advanceDueDate: false).Value;

    private static HabitLog WithCreatedAt(HabitLog log, DateTime createdAtUtc)
    {
        typeof(HabitLog).GetProperty(nameof(HabitLog.CreatedAtUtc))!.SetValue(log, createdAtUtc);
        return log;
    }

    [Fact]
    public void RecentLogs_OnlyReturnsLogsForRequestedHabit()
    {
        var target = RecurringHabit();
        var other = RecurringHabit();
        var logs = new[] { Log(target, Anchor), Log(other, Anchor), Log(target, Anchor.AddDays(-1)) }.AsQueryable();

        var result = HabitLogReader.BuildRecentLogs(logs, target.Id, Anchor.AddDays(-365), 100).ToList();

        result.Should().HaveCount(2);
        result.Should().OnlyContain(l => l.HabitId == target.Id);
    }

    [Fact]
    public void RecentLogs_ExcludesLogsBeforeLookbackWindow()
    {
        var habit = RecurringHabit();
        var inWindow = Log(habit, Anchor.AddDays(-10));
        var onBoundary = Log(habit, Anchor.AddDays(-30));
        var beforeWindow = Log(habit, Anchor.AddDays(-31));
        var logs = new[] { inWindow, onBoundary, beforeWindow }.AsQueryable();

        var result = HabitLogReader.BuildRecentLogs(logs, habit.Id, Anchor.AddDays(-30), 100).ToList();

        result.Select(l => l.Id).Should().BeEquivalentTo(new[] { inWindow.Id, onBoundary.Id });
    }

    [Fact]
    public void RecentLogs_OrdersNewestDateFirst()
    {
        var habit = RecurringHabit();
        var oldest = Log(habit, Anchor.AddDays(-5));
        var middle = Log(habit, Anchor.AddDays(-2));
        var newest = Log(habit, Anchor);
        var logs = new[] { oldest, newest, middle }.AsQueryable();

        var result = HabitLogReader.BuildRecentLogs(logs, habit.Id, Anchor.AddDays(-365), 100).ToList();

        result.Select(l => l.Date).Should().ContainInOrder(newest.Date, middle.Date, oldest.Date);
    }

    [Fact]
    public void RecentLogs_SameDate_OrdersByCreatedAtDescending()
    {
        var habit = FlexibleHabit();
        var earlier = WithCreatedAt(Log(habit, Anchor), new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc));
        var later = WithCreatedAt(Log(habit, Anchor), new DateTime(2026, 7, 1, 20, 0, 0, DateTimeKind.Utc));
        var logs = new[] { earlier, later }.AsQueryable();

        var result = HabitLogReader.BuildRecentLogs(logs, habit.Id, Anchor.AddDays(-365), 100).ToList();

        result.Select(l => l.Id).Should().ContainInOrder(later.Id, earlier.Id);
    }

    [Fact]
    public void RecentLogs_CapsResultAtLimit_KeepingNewest()
    {
        var habit = RecurringHabit();
        var logs = Enumerable.Range(0, 10)
            .Select(i => Log(habit, Anchor.AddDays(-i)))
            .ToArray()
            .AsQueryable();

        var result = HabitLogReader.BuildRecentLogs(logs, habit.Id, Anchor.AddDays(-365), 3).ToList();

        result.Should().HaveCount(3);
        result.Select(l => l.Date).Should().ContainInOrder(Anchor, Anchor.AddDays(-1), Anchor.AddDays(-2));
    }

    [Fact]
    public void RecentLogs_LargeSpan_AppliesLookbackThenCapsToNewestPage()
    {
        var habit = RecurringHabit();
        var logs = Enumerable.Range(0, 400)
            .Select(i => Log(habit, Anchor.AddDays(-i)))
            .ToArray()
            .AsQueryable();

        var since = Anchor.AddDays(-365);

        var result = HabitLogReader.BuildRecentLogs(logs, habit.Id, since, 50).ToList();

        result.Should().HaveCount(50);
        result.Should().OnlyContain(l => l.Date >= since);
        result.First().Date.Should().Be(Anchor);
        result.Last().Date.Should().Be(Anchor.AddDays(-49));
        result.Select(l => l.Date).Should().BeInDescendingOrder();
    }

    [Fact]
    public void RecentLogs_LargeSpan_WithoutCapReturnsEveryLogInWindow()
    {
        var habit = RecurringHabit();
        var logs = Enumerable.Range(0, 400)
            .Select(i => Log(habit, Anchor.AddDays(-i)))
            .ToArray()
            .AsQueryable();

        var since = Anchor.AddDays(-365);

        var result = HabitLogReader.BuildRecentLogs(logs, habit.Id, since, 1000).ToList();

        result.Should().HaveCount(366);
        result.Should().OnlyContain(l => l.Date >= since);
    }

    [Fact]
    public async Task ReadRecentLogsAsync_ReturnsNewestFirstWithinLookbackScopedToHabit()
    {
        await using var context = CreateInMemoryDbContext();
        var target = RecurringHabit();
        Log(target, Anchor);
        Log(target, Anchor.AddDays(-10));
        Log(target, Anchor.AddDays(-30));
        Log(target, Anchor.AddDays(-40));
        var other = RecurringHabit();
        Log(other, Anchor);
        context.Habits.AddRange(target, other);
        await context.SaveChangesAsync();

        var result = await new HabitLogReader(context)
            .ReadRecentLogsAsync(target.Id, Anchor.AddDays(-30), 100, CancellationToken.None);

        result.Should().OnlyContain(l => l.HabitId == target.Id);
        result.Select(l => l.Date).Should().Equal(Anchor, Anchor.AddDays(-10), Anchor.AddDays(-30));
    }

    [Fact]
    public async Task ReadRecentLogsAsync_CapsAtLimitKeepingNewest()
    {
        await using var context = CreateInMemoryDbContext();
        var habit = RecurringHabit();
        foreach (var i in Enumerable.Range(0, 6))
            Log(habit, Anchor.AddDays(-i));
        context.Habits.Add(habit);
        await context.SaveChangesAsync();

        var result = await new HabitLogReader(context)
            .ReadRecentLogsAsync(habit.Id, Anchor.AddDays(-365), 3, CancellationToken.None);

        result.Select(l => l.Date).Should().Equal(Anchor, Anchor.AddDays(-1), Anchor.AddDays(-2));
    }

    [Fact]
    public async Task ReadRecentLogsAsync_ExcludesSoftDeletedLogs()
    {
        await using var context = CreateInMemoryDbContext();
        var habit = RecurringHabit();
        Log(habit, Anchor);
        Log(habit, Anchor.AddDays(-1)).SoftDelete();
        context.Habits.Add(habit);
        await context.SaveChangesAsync();

        var result = await new HabitLogReader(context)
            .ReadRecentLogsAsync(habit.Id, Anchor.AddDays(-365), 100, CancellationToken.None);

        result.Select(l => l.Date).Should().Equal(Anchor);
    }

    private static OrbitDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"HabitLogReaderTests_{Guid.NewGuid()}")
            .Options;
        return new OrbitDbContext(options);
    }
}
