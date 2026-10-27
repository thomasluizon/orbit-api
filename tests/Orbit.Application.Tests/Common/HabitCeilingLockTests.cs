using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Common;

public class HabitCeilingLockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericEntry_AtHabitCeiling_PreservesLocalizedLimit(bool isPtBr)
    {
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var users = Substitute.For<IGenericRepository<User>>();
        var config = Substitute.For<IAppConfigService>();
        var unitOfWork = Substitute.For<IUnitOfWork>().PassThroughTransactions<Result<Guid>>();
        users.GetByIdAsync(Guid.Empty, Arg.Any<CancellationToken>())
            .Returns(User.Create("Reader", "reader@orbit.test").Value);
        config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>()).Returns(37);
        habits.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(37);
        var gate = new PayGateService(habits, users, config, Substitute.For<IUserDateService>(), Substitute.For<IFeatureFlagService>());
        var mutated = false;

        var result = await HabitCeilingLock.ExecuteEntryAsync<bool, Guid>(
            unitOfWork, Guid.Empty, gate, _ => Task.FromResult(Result.Success(true)), state => state,
            (_, _) =>
            {
                mutated = true;
                return Task.FromResult(Result.Success(Guid.NewGuid()));
            }, CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.HabitLimitReached);
        result.ErrorArgs.Should().Equal(37);
        ErrorCopy.TryResolve(result.ErrorCode, isPtBr, result.ErrorArgs, out var localized).Should().BeTrue();
        localized.Should().Be(isPtBr ? "Você chegou ao limite de 37 hábitos." : "You've reached the 37 habit limit.");
        mutated.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_LocksUserRowBeforeHabitMutation()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>().PassThroughTransactions();
        var userId = Guid.NewGuid();
        var order = new List<string>();
        unitOfWork.AcquireAdvisoryLockAsync(HabitCeilingLock.ForUser(userId), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("ceiling");
                return Task.CompletedTask;
            });
        unitOfWork.AcquireUserRowLockAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("user");
                return Task.CompletedTask;
            });

        await HabitCeilingLock.ExecuteAsync(unitOfWork, userId, _ =>
        {
            order.Add("habit");
            return Task.CompletedTask;
        }, CancellationToken.None);

        order.Should().Equal("ceiling", "user", "habit");
    }

    [Fact]
    public async Task ConcurrentCreateAttempts_CommitNoMoreThanConfiguredCeiling()
    {
        const int ceiling = 1000;
        var liveHabitCount = 999;
        var unitOfWork = new SerializingUnitOfWork();
        var firstReadCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCreate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attemptCount = 0;

        Task<Result> AttemptCreate() => HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            Guid.Empty,
            async cancellationToken =>
            {
                var observedCount = liveHabitCount;
                if (Interlocked.Increment(ref attemptCount) == 1)
                {
                    firstReadCompleted.TrySetResult(true);
                    await releaseFirstCreate.Task.WaitAsync(cancellationToken);
                }

                if (observedCount + 1 > ceiling)
                    return Result.Failure("Habit limit reached.");

                liveHabitCount = observedCount + 1;
                return Result.Success();
            },
            CancellationToken.None);

        var first = AttemptCreate();
        await firstReadCompleted.Task;
        var second = AttemptCreate();
        await unitOfWork.SecondLockRequested.Task;

        second.IsCompleted.Should().BeFalse();
        releaseFirstCreate.TrySetResult(true);
        var results = await Task.WhenAll(first, second);

        liveHabitCount.Should().Be(ceiling);
        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.IsFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedCreationOverlappingCompletedRootReactivation_RefusesReactivationAtCeiling(bool isPtBr)
    {
        const int ceiling = 1000;
        var liveHabitCount = 999;
        var reactivated = false;
        var unitOfWork = new SerializingUnitOfWork();
        var habitRepository = Substitute.For<IGenericRepository<Habit>>();
        var userRepository = Substitute.For<IGenericRepository<User>>();
        var appConfig = Substitute.For<IAppConfigService>();
        var userDateService = Substitute.For<IUserDateService>();
        userRepository.GetByIdAsync(Guid.Empty, Arg.Any<CancellationToken>())
            .Returns(User.Create("Test User", "test@example.com").Value);
        appConfig.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>())
            .Returns(ceiling);
        habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => liveHabitCount);
        var payGate = new PayGateService(habitRepository, userRepository, appConfig, userDateService, Substitute.For<IFeatureFlagService>());
        var creationGateCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var creation = HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            Guid.Empty,
            async cancellationToken =>
            {
                var allowance = await payGate.CanCreateHabits(Guid.Empty, 1, cancellationToken);
                creationGateCompleted.TrySetResult(true);
                await releaseCreation.Task.WaitAsync(cancellationToken);
                if (allowance.IsFailure)
                    return allowance;

                liveHabitCount++;
                return Result.Success();
            },
            CancellationToken.None);

        await creationGateCompleted.Task;
        var reactivation = HabitCeilingLock.ExecuteEntryAsync(
            unitOfWork,
            Guid.Empty,
            payGate,
            _ => Task.FromResult(Result.Success(true)),
            entersLiveRootSet: _ => true,
            (_, _) =>
            {
                reactivated = true;
                liveHabitCount++;
                return Task.FromResult(Result.Success());
            },
            CancellationToken.None);

        await unitOfWork.SecondLockRequested.Task;
        reactivation.IsCompleted.Should().BeFalse();
        releaseCreation.TrySetResult(true);
        var creationResult = await creation;
        var reactivationResult = await reactivation;

        creationResult.IsSuccess.Should().BeTrue();
        reactivationResult.IsFailure.Should().BeTrue();
        reactivationResult.ErrorArgs.Should().Equal(ceiling);
        ErrorCopy.TryResolve(reactivationResult.ErrorCode, isPtBr, reactivationResult.ErrorArgs, out var localized).Should().BeTrue();
        localized.Should().Be(isPtBr ? "Você chegou ao limite de 1000 hábitos." : "You've reached the 1000 habit limit.");
        reactivationResult.ErrorCode.Should().Be("HABIT_LIMIT_REACHED");
        reactivationResult.Error.Should().Be("You've reached the 1000 habit limit.");
        reactivated.Should().BeFalse();
        liveHabitCount.Should().Be(ceiling);
    }
}
