using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitScheduleLogReaderTests
{
    private static readonly DateOnly DueDate = new(2026, 6, 1);

    [Fact]
    public async Task PageLoader_EmptyPageAvoidsDatabaseReads()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        counter.Reset();

        await new HabitSchedulePageLoader(factory.Context).LoadAsync([], DueDate, DueDate);

        counter.CommandCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadDaysAsync_EmptyIdsAndReversedWindowAvoidDatabaseReads()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var reader = new HabitScheduleLogReader(factory.Context);
        counter.Reset();

        (await reader.ReadDaysAsync([], DueDate, DueDate)).Should().BeEmpty();
        (await reader.ReadDaysAsync([Guid.NewGuid()], DueDate.AddDays(1), DueDate)).Should().BeEmpty();
        counter.CommandCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadDaysAsync_RejectsUnsupportedProviderBeforeExecutingSql()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"ScheduleLogs_{Guid.NewGuid()}")
            .Options;
        using var context = new OrbitDbContext(options);
        var reader = new HabitScheduleLogReader(context);

        var act = () => reader.ReadDaysAsync([Guid.NewGuid()], DueDate, DueDate);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("Schedule log aggregation requires a relational provider.");
    }

    [Fact]
    public async Task ReadDaysAsync_GroupsCompletionsAndSkipsWithinInclusiveWindow()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var user = User.Create("Schedule User", $"schedule-{Guid.NewGuid():N}@example.com").Value;
        var habit = CreateFlexibleHabit(user.Id);
        var other = CreateFlexibleHabit(user.Id);
        habit.Log(DueDate.AddDays(-1)).IsSuccess.Should().BeTrue();
        habit.Log(DueDate).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(DueDate.AddDays(1)).IsSuccess.Should().BeTrue();
        habit.Log(DueDate.AddDays(2)).Value.SoftDelete();
        habit.Log(DueDate.AddDays(3)).IsSuccess.Should().BeTrue();
        other.Log(DueDate).IsSuccess.Should().BeTrue();
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(habit, other);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();
        counter.Reset();

        var days = await new HabitScheduleLogReader(factory.Context)
            .ReadDaysAsync([habit.Id], DueDate, DueDate.AddDays(1));

        days.Should().BeEquivalentTo([
            new Orbit.Domain.Interfaces.HabitScheduleLogDay(habit.Id, DueDate, 1, 0, true),
            new Orbit.Domain.Interfaces.HabitScheduleLogDay(habit.Id, DueDate.AddDays(1), 0, 1, true)]);
        counter.CommandCount.Should().Be(1);
        var command = counter.Commands.Single();
        command.Parameters.Should().Contain(parameter => parameter.Contains(habit.Id.ToString()));
        command.Parameters.Should().Contain(DueDate.ToString("yyyy-MM-dd"));
        command.Parameters.Should().Contain(DueDate.AddDays(1).ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task ReadDaysAsync_RebuildsCountsAndPresenceFromRealRows()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Schedule User", $"schedule-{Guid.NewGuid():N}@example.com").Value;
        var habit = CreateFlexibleHabit(user.Id);
        var other = CreateFlexibleHabit(user.Id);
        var from = DueDate;
        var to = DueDate.AddDays(3);
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var logs = new[]
        {
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, from, 1, 0, now),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, from, 2, 1, now),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, from.AddDays(1), 0, 0, now),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, from.AddDays(2), -1, 0, now),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, to, 1, 0, now),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, from.AddDays(-1), 1, 0, now),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, to.AddDays(1), 1, 0, now),
            HabitLog.FromScheduleRead(Guid.NewGuid(), other.Id, from, 1, 0, now)
        };
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(habit, other);
        factory.Context.HabitLogs.AddRange(logs);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();

        var days = await new HabitScheduleLogReader(factory.Context)
            .ReadDaysAsync([habit.Id], from, to);

        days.Should().BeEquivalentTo([
            new Orbit.Domain.Interfaces.HabitScheduleLogDay(habit.Id, from, 2, 0, true),
            new Orbit.Domain.Interfaces.HabitScheduleLogDay(habit.Id, from.AddDays(1), 0, 1, true),
            new Orbit.Domain.Interfaces.HabitScheduleLogDay(habit.Id, from.AddDays(2), 0, 0, true),
            new Orbit.Domain.Interfaces.HabitScheduleLogDay(habit.Id, to, 1, 0, true)
        ]);
    }

    [Fact]
    public async Task ReadResolvedDueDateIdsAsync_ReturnsOnlyHabitsWithALogOnTheirDueDate()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Schedule User", $"schedule-{Guid.NewGuid():N}@example.com").Value;
        var completed = CreateFlexibleHabit(user.Id);
        var skipped = CreateFlexibleHabit(user.Id);
        var earlier = CreateFlexibleHabit(user.Id);
        var missing = CreateFlexibleHabit(user.Id);
        var other = CreateFlexibleHabit(user.Id);
        completed.Log(DueDate).IsSuccess.Should().BeTrue();
        skipped.SkipFlexible(DueDate).IsSuccess.Should().BeTrue();
        earlier.Log(DueDate.AddDays(-1)).IsSuccess.Should().BeTrue();
        other.Log(DueDate).IsSuccess.Should().BeTrue();
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(completed, skipped, earlier, missing, other);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();
        var reader = new HabitScheduleLogReader(factory.Context);

        (await reader.ReadResolvedDueDateIdsAsync([])).Should().BeEmpty();
        var ids = await reader.ReadResolvedDueDateIdsAsync(
            [completed.Id, skipped.Id, earlier.Id, missing.Id]);

        ids.Should().BeEquivalentTo([completed.Id, skipped.Id]);
    }

    private static Habit CreateFlexibleHabit(Guid userId) =>
        Habit.Create(new HabitCreateParams(
            userId, "Flexible", FrequencyUnit.Week, 3, DueDate, IsFlexible: true)).Value;
}
