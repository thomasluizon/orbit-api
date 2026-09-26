using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitScheduleSnapshotInvalidationTests
{
    [Fact]
    public async Task HabitSave_InvalidatesPreviouslyReadSchedule()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Snapshot User", "habit-save@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "One time", null, null, today)).Value;
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();
        var store = new HabitScheduleSnapshotStore(new GenericRepository<Habit>(factory.Context));
        HabitScheduleSnapshotInvalidation.Attach(factory.Context, store);

        var before = await store.GetAsync(user.Id, CancellationToken.None);
        before.Single().IsCompleted.Should().BeFalse();
        typeof(Habit).GetProperty(nameof(Habit.IsCompleted))!.SetValue(habit, true);
        await factory.Context.SaveChangesAsync();
        var after = await store.GetAsync(user.Id, CancellationToken.None);

        after.Should().NotBeSameAs(before);
        after.Single().IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task FailedSave_InvalidatesPreviouslyReadSchedule()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Snapshot User", "failed-save@example.com").Value;
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(Habit.Create(new HabitCreateParams(user.Id, "Daily", FrequencyUnit.Day, 1, today)).Value);
        await factory.Context.SaveChangesAsync();
        var store = new HabitScheduleSnapshotStore(new GenericRepository<Habit>(factory.Context));
        HabitScheduleSnapshotInvalidation.Attach(factory.Context, store);

        var before = await store.GetAsync(user.Id, CancellationToken.None);
        factory.Context.Users.Add(User.Create("Duplicate", "failed-save@example.com").Value);
        var save = () => factory.Context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
        factory.Context.ChangeTracker.Clear();
        var after = await store.GetAsync(user.Id, CancellationToken.None);

        after.Should().NotBeSameAs(before);
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task NonHabitSave_KeepsOneUserWideScheduleRead()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Snapshot User", "xp-save@example.com").Value;
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(Habit.Create(new HabitCreateParams(user.Id, "Daily", FrequencyUnit.Day, 1, today)).Value);
        await factory.Context.SaveChangesAsync();
        var store = new HabitScheduleSnapshotStore(new GenericRepository<Habit>(factory.Context));
        HabitScheduleSnapshotInvalidation.Attach(factory.Context, store);

        counter.Reset();
        var before = await store.GetAsync(user.Id, CancellationToken.None);
        user.SetStreakState(1, 1, today);
        await factory.Context.SaveChangesAsync();
        var after = await store.GetAsync(user.Id, CancellationToken.None);

        after.Should().BeSameAs(before);
        counter.Commands.Count(command => command.Sql.Contains("\"IsBadHabit\"", StringComparison.Ordinal)
            && command.Sql.Contains("\"IsGeneral\"", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task RolledBackTransaction_RechecksScheduleEditedBetweenAttempts()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Repair User", "repair-retry@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Daily", FrequencyUnit.Day, 1, today)).Value;
        context.Users.Add(user);
        context.Habits.Add(habit);
        await context.SaveChangesAsync();

        var snapshots = new HabitScheduleSnapshotStore(new GenericRepository<Habit>(context));
        HabitScheduleSnapshotInvalidation.Attach(context, snapshots);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings(), scheduleSnapshots: snapshots);
        var firstAttempt = () => HabitCeilingLock.ExecuteAsync(unitOfWork, user.Id, async token =>
        {
            var schedule = await snapshots.GetAsync(user.Id, token);
            schedule.Single().FrequencyUnit.Should().Be(FrequencyUnit.Day);
            user.SetStreakState(1, 1, today);
            await unitOfWork.SaveChangesAsync(token);
            throw new InvalidOperationException("Retry after a failed commit");
        }, CancellationToken.None);

        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        await using (var editor = factory.CreateContext())
        {
            var editedHabit = await editor.Habits.SingleAsync(candidate => candidate.Id == habit.Id);
            var edit = editedHabit.Update(new HabitUpdateParams(
                "Friday", null, FrequencyUnit.Day, 1, [DayOfWeek.Friday], false, today));
            edit.IsSuccess.Should().BeTrue();
            await editor.SaveChangesAsync();
        }

        var retrySchedule = await HabitCeilingLock.ExecuteAsync(unitOfWork, user.Id,
            token => snapshots.GetAsync(user.Id, token), CancellationToken.None);

        retrySchedule.Single().Days.Should().ContainSingle().Which.Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public async Task ResetTracking_RechecksScheduleEditedOutsideScope()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Repair User", "repair-reset@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Daily", FrequencyUnit.Day, 1, today)).Value;
        context.Users.Add(user);
        context.Habits.Add(habit);
        await context.SaveChangesAsync();

        var snapshots = new HabitScheduleSnapshotStore(new GenericRepository<Habit>(context));
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings(), scheduleSnapshots: snapshots);
        var before = await snapshots.GetAsync(user.Id, CancellationToken.None);

        await using (var editor = factory.CreateContext())
        {
            var editedHabit = await editor.Habits.SingleAsync(candidate => candidate.Id == habit.Id);
            editedHabit.Update(new HabitUpdateParams(
                "Friday", null, FrequencyUnit.Day, 1, [DayOfWeek.Friday], false, today))
                .IsSuccess.Should().BeTrue();
            await editor.SaveChangesAsync();
        }

        unitOfWork.ResetTracking();
        var after = await snapshots.GetAsync(user.Id, CancellationToken.None);

        after.Should().NotBeSameAs(before);
        after.Single().Days.Should().ContainSingle().Which.Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public async Task BulkLog_CompletedOneTimeHabit_DoesNotAddUnscheduledStreakDay()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var today = new DateOnly(2026, 4, 3);
        var priorMonday = new DateOnly(2026, 3, 30);
        var user = User.Create("Bulk User", "bulk-schedule@example.com").Value;
        var oneTime = Habit.Create(new HabitCreateParams(user.Id, "One time", null, null, today)).Value;
        var weeklyResult = Habit.Create(new HabitCreateParams(user.Id, "Weekly", FrequencyUnit.Week, 1,
            priorMonday));
        weeklyResult.IsSuccess.Should().BeTrue(weeklyResult.Error);
        var weekly = weeklyResult.Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(weekly, priorMonday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(oneTime, today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        weekly.Log(priorMonday, advanceDueDate: false).IsSuccess.Should().BeTrue();
        context.Users.Add(user);
        context.Habits.AddRange(oneTime, weekly);
        await context.SaveChangesAsync();

        var habits = new GenericRepository<Habit>(context);
        var logs = new GenericRepository<HabitLog>(context);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
        var snapshots = new HabitScheduleSnapshotStore(habits);
        HabitScheduleSnapshotInvalidation.Attach(context, snapshots);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        var gamification = Substitute.For<IGamificationService>();
        gamification.ProcessHabitsLogged(user.Id, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var read = await snapshots.GetAsync(user.Id, CancellationToken.None);
                read.Single(habit => habit.Id == oneTime.Id).IsCompleted.Should().BeTrue();
                await unitOfWork.SaveChangesAsync();
                return [];
            });
        var streak = new UserStreakService(
            new UserStreakRepositories(new GenericRepository<User>(context), logs,
                new GenericRepository<StreakFreeze>(context)),
            dates, Substitute.For<IFriendFeedEventEmitter>(), snapshots);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkLogHabitsCommandHandler(habits, logs,
            new BulkLogServices(dates, streak, gamification),
            new GoalCompletionService(new GenericRepository<Goal>(context), gamification, unitOfWork, dates),
            unitOfWork, cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);

        var result = await handler.Handle(new BulkLogHabitsCommand(user.Id, [new(oneTime.Id)]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Single().Status.Should().Be(BulkItemStatus.Success);
        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(1);
    }
}
