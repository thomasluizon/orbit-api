using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitLogReaderTests(ITestOutputHelper output)
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
    public async Task Handle_ExplicitPastRange_ReturnsOnlyInclusiveRangeWithoutLegacyCap()
    {
        await using var context = CreateInMemoryDbContext();
        var habit = RecurringHabit();
        var other = RecurringHabit();
        var from = new DateOnly(2025, 1, 1);
        var to = new DateOnly(2025, 1, 31);
        var createdAtUtc = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expected = Enumerable.Range(0, 1001)
            .Select(i => HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, from, 1, i, createdAtUtc.AddSeconds(i)))
            .ToList();
        expected.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, to, 1, 0, createdAtUtc));
        var deleted = HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, from, 1, 1002, createdAtUtc);
        deleted.SoftDelete();
        context.Habits.AddRange(habit, other);
        context.HabitLogs.AddRange(expected);
        context.HabitLogs.AddRange(
            deleted,
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, from.AddDays(-1), 1, 0, createdAtUtc),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, to.AddDays(1), 1, 0, createdAtUtc),
            HabitLog.FromScheduleRead(Guid.NewGuid(), other.Id, from, 1, 0, createdAtUtc));
        await context.SaveChangesAsync();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Anchor);
        var handler = new GetHabitLogsQueryHandler(
            new GenericRepository<Habit>(context), new HabitLogReader(context), dates);

        var result = await handler.Handle(new GetHabitLogsQuery(UserId, habit.Id, from, to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(log => log.Id).Should().Equal(expected
            .OrderByDescending(log => log.Date).ThenByDescending(log => log.CreatedAtUtc).Select(log => log.Id));
    }

    [Fact]
    public async Task ReadRangeLogsAsync_UsesExistingDateIndex()
    {
        var plan = new RangePlanInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(plan);
        var user = User.Create("Range User", "range@example.com").Value;
        var from = new DateOnly(2025, 1, 1);
        var to = new DateOnly(2025, 1, 31);
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Range", FrequencyUnit.Day, 1, from)).Value;
        Log(habit, from.AddDays(-1));
        Log(habit, from);
        Log(habit, to);
        Log(habit, to.AddDays(1));
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();
        plan.Enabled = true;

        var result = await new HabitLogReader(factory.Context).ReadRangeLogsAsync(habit.Id, from, to);

        output.WriteLine(plan.Sql);
        output.WriteLine(plan.Plan);
        result.Select(log => log.Date).Should().Equal(to, from);
        plan.Plan.Should().Contain("IX_HabitLogs_HabitId_Date");
    }

    private sealed class RangePlanInterceptor : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public string Sql { get; private set; } = "";
        public string Plan { get; private set; } = "";

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Enabled)
                return result;

            Sql = command.CommandText;
            await using var explain = command.Connection!.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
            foreach (DbParameter parameter in command.Parameters)
            {
                var copy = explain.CreateParameter();
                copy.ParameterName = parameter.ParameterName;
                copy.Value = parameter.Value;
                copy.DbType = parameter.DbType;
                explain.Parameters.Add(copy);
            }

            await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
            var rows = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(string.Join(" | ", values));
            }
            Plan = string.Join(Environment.NewLine, rows);
            return result;
        }
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

    [Fact]
    public async Task LastCompletionDate_IncludesRecentSubhabit()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Subhabit User", "subhabit@example.com").Value;
        var otherUser = User.Create("Other User", "other@example.com").Value;
        var parent = Habit.Create(new HabitCreateParams(user.Id, "Parent", FrequencyUnit.Day, 1, Anchor)).Value;
        var child = Habit.Create(new HabitCreateParams(
            user.Id, "Child", FrequencyUnit.Day, 1, Anchor, ParentHabitId: parent.Id)).Value;
        var otherHabit = Habit.Create(new HabitCreateParams(otherUser.Id, "Other", FrequencyUnit.Day, 1, Anchor)).Value;
        Log(parent, Anchor.AddDays(-10));
        Log(child, Anchor.AddDays(-1));
        Log(otherHabit, Anchor.AddDays(1));
        factory.Context.Users.AddRange(user, otherUser);
        factory.Context.Habits.AddRange(parent, child, otherHabit);
        await factory.Context.SaveChangesAsync();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().Be(Anchor.AddDays(-1));
    }

    [Fact]
    public async Task LastCompletionDate_IncludesRecentGeneralHabit()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("General User", "general@example.com").Value;
        var general = Habit.Create(new HabitCreateParams(
            user.Id, "General", null, null, Anchor, IsGeneral: true)).Value;
        Log(general, Anchor.AddDays(-2));
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(general);
        await factory.Context.SaveChangesAsync();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().Be(Anchor.AddDays(-2));
    }

    [Fact]
    public async Task LastCompletionDate_ExcludesNewerBadHabitSlip()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Slip User", "slip@example.com").Value;
        var goodHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Good", FrequencyUnit.Day, 1, Anchor)).Value;
        var badHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Bad", FrequencyUnit.Day, 1, Anchor, IsBadHabit: true)).Value;
        Log(goodHabit, Anchor.AddDays(-3));
        Log(badHabit, Anchor.AddDays(-1));
        badHabit.Update(new HabitUpdateParams(
            "Bad", null, FrequencyUnit.Day, 1, null, false, Anchor)).IsSuccess.Should().BeTrue();
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(goodHabit, badHabit);
        await factory.Context.SaveChangesAsync();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().Be(Anchor.AddDays(-3));
    }

    [Fact]
    public async Task LastCompletionDate_IncludesCompletionAfterHabitBecomesBad()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Former Good User", "former-good@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Former Good", FrequencyUnit.Day, 1, Anchor)).Value;
        Log(habit, Anchor.AddDays(-1));
        habit.Update(new HabitUpdateParams(
            "Former Good", null, FrequencyUnit.Day, 1, null, true, Anchor)).IsSuccess.Should().BeTrue();
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().Be(Anchor.AddDays(-1));
    }

    [Fact]
    public async Task LastCompletionDate_ReturnsNullWhenOnlyBadHabitSlipsExist()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Slip Only User", "slip-only@example.com").Value;
        var badHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Bad", FrequencyUnit.Day, 1, Anchor, IsBadHabit: true)).Value;
        Log(badHabit, Anchor);
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(badHabit);
        await factory.Context.SaveChangesAsync();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task LastCompletionDate_ExcludesSkipsAndDeletedCompletions()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Skip User", "skip@example.com").Value;
        var habit = FlexibleHabitFor(user.Id);
        Log(habit, Anchor.AddDays(-8));
        habit.SkipFlexible(Anchor.AddDays(-1)).IsSuccess.Should().BeTrue();
        Log(habit, Anchor).SoftDelete();
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().Be(Anchor.AddDays(-8));
    }

    [Fact]
    public async Task LastCompletionDate_ReturnsNullWhenOnlyActivityIsSkip()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Skip Only User", "skip-only@example.com").Value;
        var habit = FlexibleHabitFor(user.Id);
        habit.SkipFlexible(Anchor).IsSuccess.Should().BeTrue();
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task LastCompletionDate_IncludesCompletionsFromDeletedHabits()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Former Habit User", "former@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Former", FrequencyUnit.Day, 1, Anchor)).Value;
        Log(habit, Anchor.AddDays(-3));
        habit.SoftDelete();
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().Be(Anchor.AddDays(-3));
    }

    [Fact]
    public async Task LastCompletionDate_ReturnsNullWithoutCompletionsInOneQuery()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var user = User.Create("New User", "new@example.com").Value;
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        counter.Reset();

        var result = await new HabitLogReader(factory.Context)
            .GetLastCompletionDateAsync(user.Id, CancellationToken.None);

        result.Should().BeNull();
        counter.CommandCount.Should().Be(1);
    }

    private static Habit FlexibleHabitFor(Guid userId) =>
        Habit.Create(new HabitCreateParams(userId, "Flexible", FrequencyUnit.Week, 3, Anchor, IsFlexible: true)).Value;

    private static OrbitDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"HabitLogReaderTests_{Guid.NewGuid()}")
            .Options;
        return new OrbitDbContext(options);
    }
}
