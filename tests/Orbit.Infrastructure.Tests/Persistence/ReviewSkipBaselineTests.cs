using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ReviewSkipBaselineTests
{
    [Fact]
    public async Task Undo_ShouldPreserveScheduleEditCommittedBeforeSkip()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Alex", "alex@test.com").Value;
        var today = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Read", FrequencyUnit.Week, 3,
            today, IsFlexible: true)).Value;
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();

        await using var skipContext = factory.CreateContext();
        await skipContext.Habits.SingleAsync();
        var newerDueDate = today.AddDays(-1);
        await using (var editContext = factory.CreateContext())
        {
            (await editContext.Habits.SingleAsync()).PostponeTo(newerDueDate);
            await editContext.SaveChangesAsync();
        }

        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        var goals = Substitute.For<IGoalCompletionService>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var skipId = Guid.NewGuid();
        using var skipUnit = new UnitOfWork(skipContext, new DatabaseConnectionSettings());
        var skipHandler = new SkipHabitCommandHandler(Repositories(skipContext), dates, goals, skipUnit, cache);
        (await skipHandler.Handle(new SkipHabitCommand(user.Id, habit.Id, SkipId: skipId), default))
            .IsSuccess.Should().BeTrue();

        await using (var afterSkip = factory.CreateContext())
            (await afterSkip.Habits.SingleAsync()).DueDate.Should().Be(newerDueDate);

        await using var undoContext = factory.CreateContext();
        using var undoUnit = new UnitOfWork(undoContext, new DatabaseConnectionSettings());
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var undoHandler = new UndoSkipHabitCommandHandler(Repositories(undoContext), dates, goals,
            payGate, undoUnit, new HabitSkipUndoWriter(undoContext), cache);
        (await undoHandler.Handle(new UndoSkipHabitCommand(user.Id, habit.Id, skipId), default))
            .IsSuccess.Should().BeTrue();

        await using var verify = factory.CreateContext();
        (await verify.Habits.SingleAsync()).DueDate.Should().Be(newerDueDate);
        (await verify.HabitSkipUndos.SingleAsync()).PreviousDueDate.Should().Be(newerDueDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skip_ShouldPreservePendingScheduleEdit_WhenBaselineIsCurrent(bool bulk)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var (user, habit, today) = await Seed(factory);
        await using var context = factory.CreateContext();
        var tracked = await context.Habits.SingleAsync();
        var stagedDueDate = today.AddDays(-1);
        tracked.PostponeTo(stagedDueDate);
        var skipId = Guid.NewGuid();

        (await Skip(context, user.Id, habit.Id, today, bulk, skipId)).IsSuccess.Should().BeTrue();

        if (!bulk)
        {
            await using var undoContext = factory.CreateContext();
            using var unit = new UnitOfWork(undoContext, new DatabaseConnectionSettings());
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var undo = new UndoSkipHabitCommandHandler(Repositories(undoContext), Dates(user.Id, today),
                Substitute.For<IGoalCompletionService>(), Substitute.For<IPayGateService>(), unit,
                new HabitSkipUndoWriter(undoContext), cache);
            (await undo.Handle(new UndoSkipHabitCommand(user.Id, habit.Id, skipId), default))
                .IsSuccess.Should().BeTrue();
        }

        await using var verify = factory.CreateContext();
        (await verify.Habits.SingleAsync()).DueDate.Should().Be(stagedDueDate);
        context.Entry(tracked).State.Should().Be(EntityState.Unchanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skip_ShouldRefuseConflictingPendingEdit_WithoutDiscardingOrSavingIt(bool bulk)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var (user, habit, today) = await Seed(factory);
        await using var context = factory.CreateContext();
        var tracked = await context.Habits.SingleAsync();
        var stagedDueDate = today.AddDays(-1);
        tracked.PostponeTo(stagedDueDate);
        var stagedTimestamp = tracked.UpdatedAtUtc;
        var committedDueDate = today.AddDays(-2);
        await using (var editContext = factory.CreateContext())
        {
            (await editContext.Habits.SingleAsync()).PostponeTo(committedDueDate);
            await editContext.SaveChangesAsync();
        }

        var result = await Skip(context, user.Id, habit.Id, today, bulk, Guid.NewGuid());

        result.ErrorCode.Should().Be(ErrorCodes.ConcurrentUpdateConflict);
        tracked.DueDate.Should().Be(stagedDueDate);
        tracked.UpdatedAtUtc.Should().Be(stagedTimestamp);
        context.Entry(tracked).State.Should().Be(EntityState.Modified);
        await using var verify = factory.CreateContext();
        (await verify.Habits.SingleAsync()).DueDate.Should().Be(committedDueDate);
        (await verify.HabitSkipUndos.CountAsync()).Should().Be(0);
        (await verify.HabitLogs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skip_ShouldValidateTheCommittedSchedule_WhenTrackedScheduleIsStale(bool bulk)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var (user, habit, today) = await Seed(factory, flexible: false);
        await using var context = factory.CreateContext();
        await context.Habits.SingleAsync();
        var committedDueDate = today.AddDays(2);
        await using (var editContext = factory.CreateContext())
        {
            (await editContext.Habits.SingleAsync()).PostponeTo(committedDueDate);
            await editContext.SaveChangesAsync();
        }

        var result = await Skip(context, user.Id, habit.Id, today, bulk, Guid.NewGuid());

        result.ErrorCode.Should().Be(ErrorCodes.HabitNotYetDue);
        await using var verify = factory.CreateContext();
        (await verify.Habits.SingleAsync()).DueDate.Should().Be(committedDueDate);
        (await verify.HabitSkipUndos.CountAsync()).Should().Be(0);
        (await verify.HabitLogs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skip_ShouldPreservePendingChangesToOtherHabits(bool bulk)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var (user, habit, today) = await Seed(factory);
        var otherHabit = Habit.Create(new HabitCreateParams(user.Id, "Write", null, null, today)).Value;
        factory.Context.Habits.Add(otherHabit);
        await factory.Context.SaveChangesAsync();

        await using var context = factory.CreateContext();
        var trackedOther = await context.Habits.SingleAsync(h => h.Id == otherHabit.Id);
        var stagedDueDate = today.AddDays(3);
        trackedOther.PostponeTo(stagedDueDate);

        (await Skip(context, user.Id, habit.Id, today, bulk, Guid.NewGuid())).IsSuccess.Should().BeTrue();

        await using var verify = factory.CreateContext();
        (await verify.Habits.SingleAsync(h => h.Id == otherHabit.Id)).DueDate.Should().Be(stagedDueDate);
        context.Entry(trackedOther).State.Should().Be(EntityState.Unchanged);
    }

    private static async Task<(User User, Habit Habit, DateOnly Today)> Seed(
        SqliteOrbitDbContextFactory factory, bool flexible = true)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        var today = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Read",
            flexible ? FrequencyUnit.Week : FrequencyUnit.Day, flexible ? 3 : 1,
            today, IsFlexible: flexible)).Value;
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();
        return (user, habit, today);
    }

    private static IUserDateService Dates(Guid userId, DateOnly today)
    {
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        dates.GetUserWeekStartDayAsync(userId, Arg.Any<CancellationToken>()).Returns(1);
        return dates;
    }

    private static async Task<Result> Skip(
        OrbitDbContext context, Guid userId, Guid habitId, DateOnly today, bool bulk, Guid skipId)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var unit = new UnitOfWork(context, new DatabaseConnectionSettings());
        if (!bulk)
            return await new SkipHabitCommandHandler(Repositories(context), Dates(userId, today),
                Substitute.For<IGoalCompletionService>(), unit, cache)
                .Handle(new SkipHabitCommand(userId, habitId, SkipId: skipId), default);

        var result = await new BulkSkipHabitsCommandHandler(new GenericRepository<Habit>(context),
            new GenericRepository<HabitLog>(context), Dates(userId, today), unit, cache)
            .Handle(new BulkSkipHabitsCommand(userId, [new BulkSkipItem(habitId)]), default);
        if (result.IsFailure)
            return result;
        var item = result.Value.Results.Single();
        if (item.Status == BulkItemStatus.Success)
            return Result.Success();
        item.Error.Should().NotBeNullOrEmpty();
        item.ErrorCode.Should().NotBeNullOrEmpty();
        return Result.Failure(item.Error!, item.ErrorCode!);
    }

    private static SkipHabitRepositories Repositories(OrbitDbContext context) => new(
        new GenericRepository<Habit>(context), new GenericRepository<HabitLog>(context),
        new GenericRepository<HabitSkipUndo>(context));
}
