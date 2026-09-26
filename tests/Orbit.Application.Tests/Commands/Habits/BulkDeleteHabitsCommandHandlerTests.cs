using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class BulkDeleteHabitsCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly BulkDeleteHabitsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public BulkDeleteHabitsCommandHandlerTests()
    {
        _handler = new BulkDeleteHabitsCommandHandler(_habitRepo, _userStreakService, _unitOfWork, _userDateService, _cache);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        _userStreakService.RecalculateAsync(UserId, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(0, 0, null));
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
    public async Task Handle_ValidIds_DeletesAllHabits()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "Habit 2", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;

        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit1, habit2 });

        var command = new BulkDeleteHabitsCommand(UserId, new List<Guid> { habit1.Id, habit2.Id });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(2);
        result.Value.Results.Should().AllSatisfy(r => r.Status.Should().Be(BulkItemStatus.Success));
        habit1.IsDeleted.Should().BeTrue();
        habit2.IsDeleted.Should().BeTrue();
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Parent_DeletesChildNotScheduledTodayAndReportsBothIds()
    {
        var parent = Habit.Create(new HabitCreateParams(UserId, "Parent", FrequencyUnit.Day, 1, Today)).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Later task", null, null, Today.AddDays(1), ParentHabitId: parent.Id)).Value;
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child });

        var result = await _handler.Handle(new BulkDeleteHabitsCommand(UserId, [parent.Id]), CancellationToken.None);

        parent.IsDeleted.Should().BeTrue();
        child.IsDeleted.Should().BeTrue();
        child.DeletedAtUtc.Should().Be(parent.DeletedAtUtc);
        result.Value.Results.Should().ContainSingle()
            .Which.Should().Match<BulkDeleteItemResult>(item =>
                item.Index == 0 && item.HabitId == parent.Id && item.Status == BulkItemStatus.Success);
        var response = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        response.GetProperty("Results")[0].GetProperty("CascadedHabitIds")
            .EnumerateArray().Select(id => id.GetGuid()).Should().Equal(child.Id);
    }

    [Fact]
    public async Task Handle_ParentWithMoreThan100Descendants_DeletesInOneRequest()
    {
        var parent = Habit.Create(new HabitCreateParams(UserId, "Parent", FrequencyUnit.Day, 1, Today)).Value;
        var habits = new List<Habit> { parent };
        for (var branch = 0; branch < 10; branch++)
        {
            var child = Habit.Create(new HabitCreateParams(
                UserId, $"Child {branch}", FrequencyUnit.Day, 1, Today, ParentHabitId: parent.Id)).Value;
            habits.Add(child);
            for (var leaf = 0; leaf < 10; leaf++)
                habits.Add(Habit.Create(new HabitCreateParams(
                    UserId, $"Leaf {branch}-{leaf}", FrequencyUnit.Day, 1, Today, ParentHabitId: child.Id)).Value);
        }
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(habits);

        var result = await _handler.Handle(new BulkDeleteHabitsCommand(UserId, [parent.Id]), CancellationToken.None);

        habits.Should().HaveCount(111).And.OnlyContain(habit => habit.IsDeleted);
        result.Value.Results.Should().ContainSingle().Which.HabitId.Should().Be(parent.Id);
        var response = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        response.GetProperty("Results")[0].GetProperty("CascadedHabitIds")
            .EnumerateArray().Select(id => id.GetGuid()).Should().BeEquivalentTo(habits.Skip(1).Select(habit => habit.Id));
    }

    [Fact]
    public async Task Handle_OverlappingRequestedSubtrees_DeletesEachHabitOnce()
    {
        var parent = Habit.Create(new HabitCreateParams(UserId, "Parent", FrequencyUnit.Day, 1, Today)).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", FrequencyUnit.Day, 1, Today, ParentHabitId: parent.Id)).Value;
        var grandchild = Habit.Create(new HabitCreateParams(
            UserId, "Grandchild", FrequencyUnit.Day, 1, Today, ParentHabitId: child.Id)).Value;
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child, grandchild });

        var result = await _handler.Handle(
            new BulkDeleteHabitsCommand(UserId, [parent.Id, child.Id, parent.Id]), CancellationToken.None);

        result.Value.Results.Select(item => item.HabitId).Should().Equal(parent.Id, child.Id, parent.Id);
        result.Value.Results.Select(item => item.Index).Should().Equal(0, 1, 2);
        result.Value.Results.Should().OnlyContain(item => item.Status == BulkItemStatus.Success);
        var response = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        response.GetProperty("Results")[0].GetProperty("CascadedHabitIds")
            .EnumerateArray().Select(id => id.GetGuid()).Should().Equal(child.Id, grandchild.Id);
        new[] { parent, child, grandchild }.Should().OnlyContain(habit => habit.IsDeleted);
        await _userStreakService.Received(1).RecalculateAsync(UserId, cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SomeNotFound_ReportsPartialFailure()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var missingId = Guid.NewGuid();

        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit1 });

        var command = new BulkDeleteHabitsCommand(UserId, new List<Guid> { habit1.Id, missingId });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[1].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[1].Error.Should().Contain("not found");
        habit1.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_AllNotFound_ReportsAllFailed()
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());

        var command = new BulkDeleteHabitsCommand(UserId, new List<Guid> { Guid.NewGuid(), Guid.NewGuid() });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().AllSatisfy(r => r.Status.Should().Be(BulkItemStatus.Failed));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });

        var cacheKey = $"summary:{UserId}:{Today:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new BulkDeleteHabitsCommand(UserId, new List<Guid> { habit.Id });

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_SaveFailsMidBatch_RollsBackWholeBatchAndSkipsRecalcAndCacheInvalidation()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "Habit 2", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit1, habit2 });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db failure mid-batch"));

        var cacheKey = $"summary:{UserId}:{Today:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new BulkDeleteHabitsCommand(UserId, new List<Guid> { habit1.Id, habit2.Id });

        var act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _userStreakService.DidNotReceive().RecalculateAsync(Arg.Any<Guid>(), cancellationToken: Arg.Any<CancellationToken>());
        _cache.TryGetValue(cacheKey, out _).Should().BeTrue();
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PersistenceAndRecalcRunInsideTransaction_NotOutsideIt()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });

        var insideTransaction = false;
        var saveObservedInsideTransaction = new List<bool>();
        var recalcObservedInsideTransaction = new List<bool>();

        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                insideTransaction = true;
                try
                {
                    await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1));
                }
                finally
                {
                    insideTransaction = false;
                }
            });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { saveObservedInsideTransaction.Add(insideTransaction); return 1; });
        _userStreakService.RecalculateAsync(UserId, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => { recalcObservedInsideTransaction.Add(insideTransaction); return new UserStreakState(0, 0, null); });

        var command = new BulkDeleteHabitsCommand(UserId, new List<Guid> { habit.Id });

        await _handler.Handle(command, CancellationToken.None);

        saveObservedInsideTransaction.Should().NotBeEmpty().And.OnlyContain(observed => observed);
        recalcObservedInsideTransaction.Should().NotBeEmpty().And.OnlyContain(observed => observed);
        habit.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_LastLinkedHabit_ClearsDerivedGoalInsideBatchTransaction()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Exercise",
            FrequencyUnit.Day,
            1,
            DueDate: Today)).Value;
        var goal = Goal.Create(UserId, "Exercise", 10, "sessions").Value;
        goal.AddHabit(habit);
        goal.SyncStandardProgress(4).IsSuccess.Should().BeTrue();
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });

        var result = await _handler.Handle(
            new BulkDeleteHabitsCommand(UserId, [habit.Id]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.IsDeleted.Should().BeTrue();
        habit.Goals.Should().BeEmpty();
        goal.Habits.Should().BeEmpty();
        goal.CurrentValue.Should().Be(0);
        goal.IsProgressDerived.Should().BeFalse();
    }
}
