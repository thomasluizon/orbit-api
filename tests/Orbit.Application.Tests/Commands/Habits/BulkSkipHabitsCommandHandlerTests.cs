using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class BulkSkipHabitsCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly BulkSkipHabitsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public BulkSkipHabitsCommandHandlerTests()
    {
        _habitRepo.TryRefreshAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>()).Returns(true);
        _handler = new BulkSkipHabitsCommandHandler(
            _habitRepo, _habitLogRepo, _userDateService, _unitOfWork, _cache);

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<bool>>>(0)(call.ArgAt<CancellationToken>(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_GeneralMembers_RejectsOnlyGeneralWithoutMutatingThem(bool mixed)
    {
        var general = Habit.Create(new HabitCreateParams(HabitSchedule.General, UserId, "Read", null, null, Today)).Value;
        var oneTime = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Task", null, null, Today)).Value;
        var recurring = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Daily", FrequencyUnit.Day, 1, Today)).Value;
        var before = (general.NextDueDate, general.ScheduledStartDate, general.IsCompleted, general.UpdatedAtUtc);
        SetupHabitsForUser(mixed ? [general, oneTime, recurring] : [general]);
        IReadOnlyList<BulkSkipItem> items = mixed ? [new(general.Id), new(oneTime.Id), new(recurring.Id)] : [new(general.Id)];

        var result = await _handler.Handle(new BulkSkipHabitsCommand(UserId, items), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(items.Count);
        result.Value.Results.Count(item => item.Status == BulkItemStatus.Success).Should().Be(mixed ? 2 : 0);
        var rejected = result.Value.Results.Single(item => item.HabitId == general.Id);
        rejected.Index.Should().Be(0);
        rejected.Status.Should().Be(BulkItemStatus.Failed);
        rejected.ErrorCode.Should().Be("CANNOT_SKIP_GENERAL_HABIT");
        (general.NextDueDate, general.ScheduledStartDate, general.IsCompleted, general.UpdatedAtUtc).Should().Be(before);
        general.Logs.Should().BeEmpty();
        await _habitLogRepo.DidNotReceive().AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(mixed ? 1 : 0).SaveChangesAsync(Arg.Any<CancellationToken>());
        if (mixed)
        {
            oneTime.NextDueDate.Should().Be(Today.AddDays(1));
            recurring.NextDueDate.Should().BeAfter(Today);
        }
    }

    [Fact]
    public async Task Handle_HabitChangedConcurrently_ReturnsConflictWithoutSaving()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });
        _habitRepo.TryRefreshAsync(habit, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(
            new BulkSkipHabitsCommand(UserId, [new(habit.Id)]), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorMessages.ConcurrentUpdateConflict.Code);
        habit.NextDueDate.Should().Be(Today);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RecurringHabitsDue_SkipsAndAdvancesDueDate()
    {
        var habit1 = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Habit 2", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        SetupHabitsForUser(new List<Habit> { habit1, habit2 });

        var items = new List<BulkSkipItem>
        {
            new(habit1.Id),
            new(habit2.Id)
        };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(2);
        result.Value.Results.Should().AllSatisfy(r => r.Status.Should().Be(BulkItemStatus.Success));
        habit1.NextDueDate.Should().BeAfter(Today);
        habit2.NextDueDate.Should().BeAfter(Today);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OverdueHabit_NotScheduledToday_SkipsAsToday()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Overdue", FrequencyUnit.Week, 1, DueDate: Today.AddDays(-4))).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkSkipItem> { new(habit.Id) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        habit.NextDueDate.Should().BeAfter(Today);
    }

    [Fact]
    public async Task Handle_IntervalChangedAfterAdvancement_SkipsDayAfterMissedActiveOccurrence()
    {
        var anchor = new DateOnly(2025, 1, 6);
        var catchUpDay = anchor.AddDays(15);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "Alternating", FrequencyUnit.Day, 1, DueDate: anchor,
            Days: [DayOfWeek.Monday])).Value;
        habit.AdvanceDueDate(anchor.AddDays(6), weekStartDay: 1);
        habit.Update(new HabitUpdateParams(habit.Schedule,
            habit.Title, habit.Description, habit.FrequencyUnit, habit.FrequencyQuantity,
            habit.Days.ToList(), habit.IsBadHabit, DueDate: null,
            UserToday: anchor.AddDays(8), IntervalWeeks: 2));
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(catchUpDay);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        SetupHabitsForUser([habit]);

        var result = await _handler.Handle(
            new BulkSkipHabitsCommand(UserId, [new BulkSkipItem(habit.Id)]),
            CancellationToken.None);

        result.Value.Results.Should().ContainSingle(r => r.Status == BulkItemStatus.Success);
        habit.NextDueDate.Should().BeAfter(catchUpDay);
    }

    [Fact]
    public async Task Handle_OneTimeTask_PostponesToTomorrow()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Task", null, null, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkSkipItem> { new(habit.Id) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        habit.NextDueDate.Should().Be(Today.AddDays(1));
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReportsFailedItem()
    {
        var missingId = Guid.NewGuid();
        SetupHabitsForUser(new List<Habit>());

        var items = new List<BulkSkipItem> { new(missingId) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Be(ErrorMessages.HabitNotFound.Message);
    }

    [Fact]
    public async Task Handle_CompletedHabit_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Task", null, null, DueDate: Today)).Value;
        habit.Log(Today); SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkSkipItem> { new(habit.Id) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Be(ErrorMessages.CannotSkipCompletedHabit.Message);
    }

    [Fact]
    public async Task Handle_FutureDate_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var futureDate = Today.AddDays(5);
        var items = new List<BulkSkipItem> { new(habit.Id, futureDate) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Be(ErrorMessages.CannotSkipFutureDate.Message);
    }

    [Fact]
    public async Task Handle_NotYetDue_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "Future habit", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(5))).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkSkipItem> { new(habit.Id, Today) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Be(ErrorMessages.HabitNotYetDue.Message);
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Task", null, null, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var cacheKey = $"summary:{UserId}:{Today:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var items = new List<BulkSkipItem> { new(habit.Id) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MixedResults_ReportsPartialSuccess()
    {
        var validHabit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "Valid", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var completedHabit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime,
            UserId, "Done", null, null, DueDate: Today)).Value;
        completedHabit.Log(Today);
        SetupHabitsForUser(new List<Habit> { validHabit, completedHabit });

        var items = new List<BulkSkipItem>
        {
            new(validHabit.Id),
            new(completedHabit.Id),
            new(Guid.NewGuid())        };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[1].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[2].Status.Should().Be(BulkItemStatus.Failed);
    }

    [Fact]
    public async Task Handle_SaveFailsMidBatch_RunsInsideTransactionAndPropagates()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db failure"));

        var command = new BulkSkipHabitsCommand(UserId, new List<BulkSkipItem> { new(habit.Id) });

        var act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MixedWindowResults_SkipsInWindowReportsBeyondWindowFailure()
    {
        var inWindow = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "In window", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var stale = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "Stale", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(-(AppConstants.DefaultOverdueWindowDays + 3)))).Value;
        SetupHabitsForUser(new List<Habit> { inWindow, stale });

        var beyondWindow = Today.AddDays(-(AppConstants.DefaultOverdueWindowDays + 3));
        var items = new List<BulkSkipItem>
        {
            new(inWindow.Id),
            new(stale.Id, beyondWindow)
        };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[1].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[1].ErrorCode.Should().Be(ErrorCodes.BeyondOverdueWindow);
        inWindow.NextDueDate.Should().BeAfter(Today);
    }

    private void SetupHabitsForUser(List<Habit> habits)
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_UsesScheduleForSkip(bool flexible)
    {
        var habit = Habit.Create(new HabitCreateParams(flexible ? HabitSchedule.Flexible : HabitSchedule.Recurring, UserId, "Habit", FrequencyUnit.Day,
            flexible ? 2 : 1, Today)).Value;
        SetupHabitsForUser([habit]);

        var result = await _handler.Handle(new BulkSkipHabitsCommand(UserId, [new(habit.Id)]), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().ContainSingle().Which.Status.Should().Be(BulkItemStatus.Success);
        habit.NextDueDate.Should().Be(flexible ? Today : Today.AddDays(1));
        habit.StartDate.Should().Be(Today);
        habit.Logs.Count(log => log.Value == 0).Should().Be(flexible ? 1 : 0);
    }
}
