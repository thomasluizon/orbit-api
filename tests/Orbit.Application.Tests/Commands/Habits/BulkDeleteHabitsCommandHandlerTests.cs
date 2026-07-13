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
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
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
    public async Task Handle_SomeNotFound_ReportsPartialFailure()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var missingId = Guid.NewGuid();

        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
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
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit1, habit2 });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db failure mid-batch"));

        var cacheKey = $"summary:{UserId}:{Today:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new BulkDeleteHabitsCommand(UserId, new List<Guid> { habit1.Id, habit2.Id });

        var act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _userStreakService.DidNotReceive().RecalculateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(_ => { recalcObservedInsideTransaction.Add(insideTransaction); return new UserStreakState(0, 0, null); });

        var command = new BulkDeleteHabitsCommand(UserId, new List<Guid> { habit.Id });

        await _handler.Handle(command, CancellationToken.None);

        saveObservedInsideTransaction.Should().NotBeEmpty().And.OnlyContain(observed => observed);
        recalcObservedInsideTransaction.Should().NotBeEmpty().And.OnlyContain(observed => observed);
        habit.IsDeleted.Should().BeTrue();
    }
}
