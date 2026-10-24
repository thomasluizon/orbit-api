using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Habits;

public sealed class BulkMoveHabitParentCommandHandlerTests : IDisposable
{
    private readonly Guid _userId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);
    private readonly IGenericRepository<Habit> _repository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IAppConfigService _config = Substitute.For<IAppConfigService>();
    private readonly IUserStreakService _streak = Substitute.For<IUserStreakService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly BulkMoveHabitParentCommandHandler _handler;
    private bool _insideTransaction;

    public BulkMoveHabitParentCommandHandlerTests()
    {
        _handler = new(_repository, _config, _streak, _unitOfWork, _dates, _cache);
        _config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(5);
        _config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>()).Returns(100);
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Today);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                _insideTransaction = true;
                try { await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)); }
                finally { _insideTransaction = false; }
            });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _insideTransaction.Should().BeTrue();
            return 1;
        });
        _streak.RecalculateAsync(_userId, cancellationToken: Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _insideTransaction.Should().BeTrue();
            return new UserStreakState(0, 0, null);
        });
    }

    private Habit Create(Guid? parent = null, bool general = false, Guid? owner = null) =>
        Habit.Create(new HabitCreateParams(general ? HabitSchedule.General : ((FrequencyUnit?)(general ? null : (FrequencyUnit?)FrequencyUnit.Day)) is null ? HabitSchedule.OneTime : HabitSchedule.Recurring, owner ?? _userId, "Habit", general ? null : FrequencyUnit.Day,
            general ? null : 1, Today, ParentHabitId: parent)).Value;

    private void Load(params Habit[] habits) => _repository.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
        .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());

    private Task<Result<BulkMoveHabitParentResult>> Move(Guid? parent, params Guid[] ids) =>
        _handler.Handle(new(_userId, ids, parent), CancellationToken.None);

    [Fact]
    public async Task ThreeValidIds_AllMoveAndRecalculateInsideOneTransaction()
    {
        var parent = Create();
        var habits = Enumerable.Range(0, 3).Select(_ => Create()).ToArray();
        Load([parent, .. habits]);
        _cache.Set($"summary:{_userId}:{Today:yyyy-MM-dd}:en", "cached");

        var result = await Move(parent.Id, habits.Select(h => h.Id).ToArray());

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Select(r => r.Index).Should().Equal(0, 1, 2);
        result.Value.Results.Should().OnlyContain(r => r.Status == BulkItemStatus.Success);
        habits.Should().OnlyContain(h => h.ParentHabitId == parent.Id);
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).AcquireAdvisoryLockAsync(HabitCeilingLock.ForUser(_userId), Arg.Any<CancellationToken>());
        await _streak.Received(1).RecalculateAsync(_userId, cancellationToken: Arg.Any<CancellationToken>());
        _cache.TryGetValue($"summary:{_userId}:{Today:yyyy-MM-dd}:en", out _).Should().BeFalse();
    }

    [Fact]
    public async Task MixedBatch_ReportsErrorsByIndexAndMovesValidHabit()
    {
        var ancestor = Create();
        var target = Create(ancestor.Id);
        var valid = Create();
        var foreign = Create(owner: Guid.NewGuid());
        Load(ancestor, target, valid, foreign);

        var result = await Move(target.Id, valid.Id, target.Id, ancestor.Id, foreign.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Select(r => r.ErrorCode).Should().Equal(
            null, ErrorCodes.SelfParent, ErrorCodes.CircularReference, ErrorCodes.HabitNotFound);
        result.Value.Results.Select(r => r.Index).Should().Equal(0, 1, 2, 3);
        valid.ParentHabitId.Should().Be(target.Id);
        ancestor.ParentHabitId.Should().BeNull();
        foreign.ParentHabitId.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentAndChild_UseFinalTreeAtDepthLimit(bool childFirst)
    {
        _config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(2);
        var parent = Create();
        var child = Create(parent.Id);
        var target = Create();
        Load(parent, child, target);

        var result = await Move(target.Id, childFirst ? [child.Id, parent.Id] : [parent.Id, child.Id]);

        result.Value.Results.Should().OnlyContain(r => r.Status == BulkItemStatus.Success);
        parent.ParentHabitId.Should().Be(target.Id);
        child.ParentHabitId.Should().Be(target.Id);
    }

    [Fact]
    public async Task MultipleCycleCandidates_DoNotRejectUnrelatedMoveOrLoop()
    {
        var root = Create();
        var child = Create(root.Id);
        var target = Create(child.Id);
        var valid = Create();
        Load(root, child, target, valid);

        var result = await Move(target.Id, valid.Id, root.Id, child.Id);

        result.Value.Results.Select(r => r.ErrorCode).Should().Equal(null, ErrorCodes.CircularReference, ErrorCodes.CircularReference);
        valid.ParentHabitId.Should().Be(target.Id);
    }

    [Fact]
    public async Task NullParent_MakesNestedHabitsRoots()
    {
        var root = Create();
        var child = Create(root.Id);
        var leaf = Create(child.Id);
        Load(root, child, leaf);

        var result = await Move(null, child.Id, leaf.Id);

        result.Value.Results.Should().OnlyContain(r => r.Status == BulkItemStatus.Success);
        child.ParentHabitId.Should().BeNull();
        leaf.ParentHabitId.Should().BeNull();
    }

    [Fact]
    public async Task NullParent_RespectsRemainingRootCapacityAndDuplicateIds()
    {
        _config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>()).Returns(2);
        var root = Create();
        var first = Create(root.Id);
        var second = Create(root.Id);
        Load(root, first, second);

        var result = await Move(null, first.Id, first.Id, second.Id, root.Id);

        result.Value.Results.Select(r => r.ErrorCode).Should().Equal(null, null, ErrorCodes.HabitLimitReached, null);
        first.ParentHabitId.Should().BeNull();
        second.ParentHabitId.Should().Be(root.Id);
    }

    [Fact]
    public async Task Depth_RejectsOnlySubtreeThatWouldExceedLimit()
    {
        _config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(3);
        var root = Create();
        var target = Create(root.Id);
        var subtree = Create();
        var leaf = Create(subtree.Id);
        var valid = Create();
        Load(root, target, subtree, leaf, valid);

        var result = await Move(target.Id, subtree.Id, valid.Id);

        result.Value.Results.Select(r => r.ErrorCode).Should().Equal(ErrorCodes.MaxDepthReached, null);
        subtree.ParentHabitId.Should().BeNull();
        leaf.ParentHabitId.Should().Be(subtree.Id);
        valid.ParentHabitId.Should().Be(target.Id);
    }

    [Fact]
    public async Task MissingTargetAndGeneralMismatch_ArePerItemFailures()
    {
        var general = Create(general: true);
        var normal = Create();
        Load(general, normal);

        var missing = await Move(Guid.NewGuid(), normal.Id);
        missing.Value.Results[0].ErrorCode.Should().Be(ErrorCodes.TargetParentNotFound);
        var mismatch = await Move(general.Id, normal.Id);
        mismatch.Value.Results[0].ErrorCode.Should().Be(ErrorCodes.GeneralMismatchWithParent);
        normal.ParentHabitId.Should().BeNull();
        await _streak.DidNotReceive().RecalculateAsync(_userId, cancellationToken: Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task BatchSize_DoesNotIncreaseHabitReads(int count)
    {
        var target = Create();
        var habits = Enumerable.Range(0, count).Select(_ => Create()).ToArray();
        Load([target, .. habits]);

        var result = await Move(target.Id, habits.Select(h => h.Id).ToArray());

        result.Value.Results.Should().HaveCount(count).And.OnlyContain(r => r.Status == BulkItemStatus.Success);
        await _repository.Received(1).FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveFailure_PropagatesWithinTransactionAndLeavesCacheIntact()
    {
        var habit = Create();
        var target = Create();
        Load(habit, target);
        _cache.Set($"summary:{_userId}:{Today:yyyy-MM-dd}:en", "cached");
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("save failed"));

        var act = () => Move(target.Id, habit.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _streak.DidNotReceive().RecalculateAsync(_userId, cancellationToken: Arg.Any<CancellationToken>());
        _cache.TryGetValue($"summary:{_userId}:{Today:yyyy-MM-dd}:en", out _).Should().BeTrue();
    }

    [Fact]
    public async Task StreakConcurrencyFailure_RetriesRecalculation()
    {
        var habit = Create();
        var target = Create();
        Load(habit, target);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => 1, _ => throw new DbUpdateConcurrencyException(), _ => 1);

        var result = await Move(target.Id, habit.Id);

        result.IsSuccess.Should().BeTrue();
        await _streak.Received(2).RecalculateAsync(_userId, cancellationToken: Arg.Any<CancellationToken>());
        _unitOfWork.Received(1).ResetTracking();
    }

    public void Dispose() => _cache.Dispose();
}
