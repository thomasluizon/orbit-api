using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Commands.Habits;

public sealed class UndoSkipHabitCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IGoalCompletionService _goals = Substitute.For<IGoalCompletionService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public UndoSkipHabitCommandHandlerTests()
    {
        _dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _dates.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(FrequencyUnit.Day, false)]
    [InlineData(FrequencyUnit.Week, true)]
    [InlineData(FrequencyUnit.Month, false)]
    public async Task SkipThenUndo_AcrossContexts_RestoresScheduleAndExistingLogs(FrequencyUnit? frequency, bool flexible)
    {
        var dueDate = frequency == FrequencyUnit.Month ? new DateOnly(2026, 3, 31) : Today;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Read", frequency, flexible ? 3 : 1,
            dueDate, IsFlexible: flexible)).Value;
        var existing = habit.Log(Today.AddDays(-4), "Keep this note", advanceDueDate: false).Value;
        if (frequency is null)
            habit.Unlog(existing.Date);
        var deleted = habit.Log(Today.AddDays(-3), "Existing tombstone", advanceDueDate: false).Value;
        habit.Unlog(deleted.Date);

        var previousCompleted = habit.IsCompleted;
        var skipId = Guid.NewGuid();
        await Seed(habit);
        await using var baseline = Context();
        var beforeLogs = (await baseline.HabitLogs.IgnoreQueryFilters().ToListAsync()).Select(LogSnapshot).ToArray();

        (await Skip(habit.Id, skipId)).IsSuccess.Should().BeTrue();
        (await Undo(habit.Id, skipId)).IsSuccess.Should().BeTrue();

        await using var context = Context();
        var restored = await context.Habits.SingleAsync(h => h.Id == habit.Id);
        restored.DueDate.Should().Be(habit.DueDate);
        restored.ScheduledStartDate.Should().Be(habit.ScheduledStartDate);
        restored.OriginalDayOfMonth.Should().Be(habit.OriginalDayOfMonth);
        restored.IsCompleted.Should().Be(previousCompleted);
        var logs = await context.HabitLogs.IgnoreQueryFilters().Where(log => log.HabitId == habit.Id).ToListAsync();
        logs.Where(log => beforeLogs.Any(before => before.Id == log.Id)).Select(LogSnapshot)
            .Should().BeEquivalentTo(beforeLogs);
        if (flexible)
        {
            var undoTombstone = logs.Single(log => !beforeLogs.Any(before => before.Id == log.Id));
            undoTombstone.Value.Should().Be(0);
            undoTombstone.IsDeleted.Should().BeTrue();
            undoTombstone.DeletedAtUtc.Should().NotBeNull();
        }
        else
            logs.Should().HaveCount(beforeLogs.Length);
    }

    [Fact]
    public async Task SecondUndo_AfterAnotherChange_IsNoOp()
    {
        var habit = CreateHabit();
        var skipId = Guid.NewGuid();
        await Seed(habit);
        (await Skip(habit.Id, skipId)).IsSuccess.Should().BeTrue();
        (await Undo(habit.Id, skipId)).IsSuccess.Should().BeTrue();
        await using (var context = Context())
        {
            var changed = await context.Habits.SingleAsync();
            changed.PostponeTo(Today.AddDays(9));
            await context.SaveChangesAsync();
        }
        _goals.ClearReceivedCalls();

        (await Undo(habit.Id, skipId)).IsSuccess.Should().BeTrue();

        await using var verify = Context();
        (await verify.Habits.SingleAsync()).DueDate.Should().Be(Today.AddDays(9));
        await _goals.DidNotReceiveWithAnyArgs().SyncDerivedGoalsAsync(default, default!, default);
    }

    [Theory]
    [InlineData("postpone")]
    [InlineData("log")]
    [InlineData("log tombstone")]
    [InlineData("skip")]
    [InlineData("delete")]
    [InlineData("tag")]
    public async Task Undo_AfterNewerChange_IsRefusedWithoutWriting(string change)
    {
        var habit = CreateHabit(flexible: change == "skip");
        var priorLog = habit.Log(Today.AddDays(-1), advanceDueDate: false).Value;
        var skipId = Guid.NewGuid();
        await Seed(habit);
        (await Skip(habit.Id, skipId)).IsSuccess.Should().BeTrue();
        await using (var context = Context())
        {
            var changed = await context.Habits.Include(h => h.Logs).SingleAsync();
            switch (change)
            {
                case "postpone": changed.PostponeTo(Today.AddDays(7)); break;
                case "log": context.HabitLogs.Add(changed.Log(Today.AddDays(1)).Value); break;
                case "log tombstone": (await context.HabitLogs.SingleAsync(log => log.Id == priorLog.Id)).SoftDelete(); break;
                case "skip": context.HabitLogs.Add(changed.SkipFlexible(Today).Value); break;
                case "delete": changed.SoftDelete(); break;
                case "tag":
                    var tag = Tag.Create(UserId, "Study", "#7c3aed").Value;
                    context.Tags.Add(tag);
                    changed.AddTag(tag);
                    break;
            }
            await context.SaveChangesAsync();
        }
        await using var before = Context();
        var beforeHabit = await before.Habits.IgnoreQueryFilters().SingleAsync();
        var beforeLogs = await before.HabitLogs.IgnoreQueryFilters().Select(log => log.UpdatedAtUtc).ToArrayAsync();

        var result = await Undo(habit.Id, skipId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().BeOneOf(DomainErrors.SkipUndoConflict.Code, ErrorCodes.HabitNotFound);
        await using var verify = Context();
        var afterHabit = await verify.Habits.IgnoreQueryFilters().SingleAsync();
        afterHabit.DueDate.Should().Be(beforeHabit.DueDate);
        afterHabit.UpdatedAtUtc.Should().Be(beforeHabit.UpdatedAtUtc);
        (await verify.HabitLogs.IgnoreQueryFilters().Select(log => log.UpdatedAtUtc).ToArrayAsync()).Should().Equal(beforeLogs);
        (await verify.HabitSkipUndos.SingleAsync()).IsUndone.Should().BeFalse();
    }

    [Fact]
    public async Task Undo_CannotUseAnotherUsersReceiptOrAnotherHabit()
    {
        var habit = CreateHabit();
        var skipId = Guid.NewGuid();
        await Seed(habit);
        (await Skip(habit.Id, skipId)).IsSuccess.Should().BeTrue();
        (await Undo(habit.Id, skipId, Guid.NewGuid())).ErrorCode.Should().Be(DomainErrors.SkipNotFound.Code);
        (await Undo(Guid.NewGuid(), skipId)).ErrorCode.Should().Be(DomainErrors.SkipNotFound.Code);
        (await Undo(habit.Id, Guid.NewGuid())).ErrorCode.Should().Be(DomainErrors.SkipNotFound.Code);
    }

    [Fact]
    public async Task Undo_LastOccurrence_RestoresCompletedFlagAndLegacyScheduleAnchor()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Read", FrequencyUnit.Day, 1,
            Today, EndDate: Today)).Value;
        var skipId = Guid.NewGuid();
        await Seed(habit);
        await using (var context = Context())
        {
            context.Entry(await context.Habits.SingleAsync()).Property(h => h.ScheduledStartDate).CurrentValue = null;
            await context.SaveChangesAsync();
        }
        (await Skip(habit.Id, skipId)).IsSuccess.Should().BeTrue();
        await using (var context = Context())
            (await context.Habits.SingleAsync()).IsCompleted.Should().BeTrue();

        (await Undo(habit.Id, skipId)).IsSuccess.Should().BeTrue();

        await using var verify = Context();
        var restored = await verify.Habits.SingleAsync();
        restored.IsCompleted.Should().BeFalse();
        restored.ScheduledStartDate.Should().BeNull();
        restored.DueDate.Should().Be(Today);
    }

    [Fact]
    public async Task RepeatingSkipId_DoesNotCreateAnotherFlexibleSkip()
    {
        var habit = CreateHabit(flexible: true);
        var skipId = Guid.NewGuid();
        await Seed(habit);
        (await Skip(habit.Id, skipId)).IsSuccess.Should().BeTrue();
        (await Skip(habit.Id, skipId)).IsSuccess.Should().BeTrue();
        await using var verify = Context();
        (await verify.HabitLogs.CountAsync()).Should().Be(1);
        (await verify.HabitSkipUndos.CountAsync()).Should().Be(1);
    }

    private static Habit CreateHabit(bool flexible = false) => Habit.Create(new HabitCreateParams(
        UserId, "Read", flexible ? FrequencyUnit.Week : FrequencyUnit.Day, flexible ? 3 : 1,
        Today, IsFlexible: flexible)).Value;

    private OrbitDbContext Context() => new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(_databaseName).Options);

    private async Task Seed(Habit habit)
    {
        await using var context = Context();
        context.Habits.Add(habit);
        await context.SaveChangesAsync();
    }

    private async Task<Result> Skip(Guid habitId, Guid skipId)
    {
        await using var context = Context();
        using var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
        var handler = new SkipHabitCommandHandler(Repositories(context), _dates, _goals, unitOfWork, _cache);
        return await handler.Handle(new SkipHabitCommand(UserId, habitId, SkipId: skipId), CancellationToken.None);
    }

    private async Task<Result> Undo(Guid habitId, Guid skipId, Guid? userId = null)
    {
        await using var context = Context();
        using var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
        var handler = new UndoSkipHabitCommandHandler(Repositories(context), _dates, _goals, _payGate, unitOfWork, new HabitSkipUndoWriter(context), _cache);
        return await handler.Handle(new UndoSkipHabitCommand(userId ?? UserId, habitId, skipId), CancellationToken.None);
    }

    private static SkipHabitRepositories Repositories(OrbitDbContext context) => new(
        new GenericRepository<Habit>(context), new GenericRepository<HabitLog>(context),
        new GenericRepository<HabitSkipUndo>(context));

    private sealed record LogSnapshotData(Guid Id, DateOnly Date, decimal Value, string? Note,
        int CompletionOrdinal, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool IsDeleted, DateTime? DeletedAtUtc);

    private static LogSnapshotData LogSnapshot(HabitLog log) => new(
        log.Id, log.Date, log.Value, log.Note, log.CompletionOrdinal,
        log.CreatedAtUtc, log.UpdatedAtUtc, log.IsDeleted, log.DeletedAtUtc);
}
