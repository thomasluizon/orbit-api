using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class BulkLogHabitsCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly BulkLogHabitsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public BulkLogHabitsCommandHandlerTests()
    {
        var services = new BulkLogServices(_userDateService, _userStreakService, _gamificationService);
        _handler = new BulkLogHabitsCommandHandler(
            _habitRepo, _habitLogRepo, services, _unitOfWork, _cache,
            Substitute.For<ILogger<BulkLogHabitsCommandHandler>>());

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _userStreakService.RecalculateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(1, 1, Today));
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task>>(0);
                var ct = call.ArgAt<CancellationToken>(1);
                return operation(ct);
            });
    }

    [Fact]
    public async Task Handle_MultipleValidHabits_LogsAllSuccessfully()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "Habit 2", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        SetupHabitsForUser(new List<Habit> { habit1, habit2 });

        var items = new List<BulkLogItem>
        {
            new(habit1.Id),
            new(habit2.Id)
        };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(2);
        result.Value.Results.Should().AllSatisfy(r => r.Status.Should().Be(BulkItemStatus.Success));
        await _habitLogRepo.Received(2).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReportsFailedItem()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var missingId = Guid.NewGuid();

        SetupHabitsForUser(new List<Habit> { habit1 });

        var items = new List<BulkLogItem>
        {
            new(habit1.Id),
            new(missingId)
        };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[1].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[1].Error.Should().Be(ErrorMessages.HabitNotFound.Message);
    }

    [Fact]
    public async Task Handle_OverdueHabit_LogsAsToday()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Overdue", FrequencyUnit.Week, 1, DueDate: Today.AddDays(-4))).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        await _habitLogRepo.Received(1).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotScheduledNotOverdue_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Future", FrequencyUnit.Week, 1, DueDate: Today.AddDays(3))).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("not scheduled");
    }

    [Fact]
    public async Task Handle_FutureDate_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var futureDate = Today.AddDays(5);
        var items = new List<BulkLogItem> { new(habit.Id, futureDate) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("future");
    }

    [Fact]
    public async Task Handle_DateBeyondOverdueWindow_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var tooOldDate = Today.AddDays(-(AppConstants.DefaultOverdueWindowDays + 1));
        var items = new List<BulkLogItem> { new(habit.Id, tooOldDate) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("overdue window");
    }

    [Fact]
    public async Task Handle_OneTimeTask_LogsAndMarksCompleted()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_CallsGamificationForSuccessfulLogs()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        await _gamificationService.Received(1).ProcessHabitsLogged(
            UserId,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == habit.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ThreeHabits_BatchesGamificationIntoSingleCall()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "Habit 2", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var habit3 = Habit.Create(new HabitCreateParams(UserId, "Habit 3", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit1, habit2, habit3 });

        var items = new List<BulkLogItem>
        {
            new(habit1.Id),
            new(habit2.Id),
            new(habit3.Id)
        };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().AllSatisfy(r => r.Status.Should().Be(BulkItemStatus.Success));
        await _gamificationService.Received(1).ProcessHabitsLogged(
            UserId,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { habit1.Id, habit2.Id, habit3.Id })),
            Arg.Any<CancellationToken>());
        await _gamificationService.DidNotReceive().ProcessHabitLogged(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _userStreakService.Received(1).RecalculateAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MixedResults_PassesOnlySuccessfulIdsToGamification()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem>
        {
            new(habit.Id),
            new(Guid.NewGuid())
        };
        var command = new BulkLogHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        await _gamificationService.Received(1).ProcessHabitsLogged(
            UserId,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == habit.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoSuccessfulLogs_SkipsStreakAndGamification()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var futureDate = Today.AddDays(5);
        var items = new List<BulkLogItem> { new(habit.Id, futureDate) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        await _userStreakService.DidNotReceive().RecalculateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _gamificationService.DidNotReceive().ProcessHabitsLogged(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var cacheKey = $"summary:{UserId}:{Today:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_AlreadyLoggedRecurringHabit_FailsScheduleCheckSinceDueDateAdvanced()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        habit.Log(Today);
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("not scheduled");
    }

    [Fact]
    public async Task Handle_SaveFailsMidBatch_RunsInsideTransactionAndPropagates()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db failure"));

        var command = new BulkLogHabitsCommand(UserId, new List<BulkLogItem> { new(habit.Id) });

        var act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidItemsInterleavedWithValid_AppliesValidReportsInvalidPerItem()
    {
        var habitA = Habit.Create(new HabitCreateParams(UserId, "Habit A", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var habitB = Habit.Create(new HabitCreateParams(UserId, "Habit B", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habitA, habitB });

        var missingId = Guid.NewGuid();
        var items = new List<BulkLogItem>
        {
            new(Guid.NewGuid(), Today.AddDays(3)),
            new(habitA.Id),
            new(missingId),
            new(habitB.Id)
        };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(4);

        result.Value.Results[0].Index.Should().Be(0);
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].ErrorCode.Should().Be(ErrorMessages.CannotLogFutureDate.Code);

        result.Value.Results[1].Index.Should().Be(1);
        result.Value.Results[1].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[1].HabitId.Should().Be(habitA.Id);
        result.Value.Results[1].LogId.Should().NotBeNull();

        result.Value.Results[2].Index.Should().Be(2);
        result.Value.Results[2].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[2].ErrorCode.Should().Be(ErrorMessages.HabitNotFound.Code);

        result.Value.Results[3].Index.Should().Be(3);
        result.Value.Results[3].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[3].HabitId.Should().Be(habitB.Id);
        result.Value.Results[3].LogId.Should().NotBeNull();

        await _habitLogRepo.Received(2).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        habitA.Logs.Should().ContainSingle(l => l.Date == Today);
        habitB.Logs.Should().ContainSingle(l => l.Date == Today);
    }

    [Fact]
    public async Task Handle_ItemPersistenceThrowsMidBatch_IsolatesFailureAndAppliesOtherItems()
    {
        var failingHabit = Habit.Create(new HabitCreateParams(UserId, "Failing", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var okHabit = Habit.Create(new HabitCreateParams(UserId, "Ok", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { failingHabit, okHabit });

        _habitLogRepo.AddAsync(
                Arg.Is<HabitLog>(l => l.HabitId == failingHabit.Id),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("insert failed"));

        var items = new List<BulkLogItem> { new(failingHabit.Id), new(okHabit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(2);

        var failing = result.Value.Results.Single(r => r.HabitId == failingHabit.Id);
        failing.Status.Should().Be(BulkItemStatus.Failed);
        failing.ErrorCode.Should().Be(ErrorMessages.BulkLogItemFailed.Code);
        failing.Error.Should().Be(ErrorMessages.BulkLogItemFailed.Message);

        var ok = result.Value.Results.Single(r => r.HabitId == okHabit.Id);
        ok.Status.Should().Be(BulkItemStatus.Success);
        ok.LogId.Should().NotBeNull();

        await _gamificationService.Received(1).ProcessHabitsLogged(
            UserId,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == okHabit.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CompletedOneTimeTask_ReportsDomainFailurePerItemAndContinuesBatch()
    {
        var task = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        task.Log(Today.AddDays(-1));
        task.IsCompleted.Should().BeTrue();

        var okHabit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { task, okHabit });

        var items = new List<BulkLogItem> { new(task.Id), new(okHabit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var taskResult = result.Value.Results.Single(r => r.HabitId == task.Id);
        taskResult.Status.Should().Be(BulkItemStatus.Failed);
        taskResult.ErrorCode.Should().Be(DomainErrors.CannotLogCompletedHabit.Code);

        result.Value.Results.Single(r => r.HabitId == okHabit.Id).Status.Should().Be(BulkItemStatus.Success);
    }

    private void SetupHabitsForUser(List<Habit> habits)
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits);
    }
}
