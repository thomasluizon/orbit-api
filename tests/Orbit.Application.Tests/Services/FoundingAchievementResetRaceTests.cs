using System.Collections.Concurrent;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Services;

public class FoundingAchievementResetRaceTests
{
    [Fact]
    public async Task ResetRequestedAfterEvidence_WaitsThenRemovesReconciledState()
    {
        var userId = Guid.NewGuid();
        var user = User.Create("Race User", $"race-{userId:N}@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        var achievements = new List<UserAchievement>();
        var evidenceSelected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowGrant = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unitOfWork = new SerializingUnitOfWork();
        var userRepository = Substitute.For<IGenericRepository<User>>();
        var achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
        var accountResetRepository = Substitute.For<IAccountResetRepository>();
        var featureFlagService = new BlockingFeatureFlagService(evidenceSelected, allowGrant);
        var foundingReader = Substitute.For<IFoundingAchievementReader>();
        var xpAwardLogRepository = Substitute.For<IGenericRepository<XpAwardLog>>();
        var userDateService = Substitute.For<IUserDateService>();

        userRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(user);
        achievementRepository.FindAsync(
                Arg.Any<Expression<Func<UserAchievement, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => achievements.ToList());
        achievementRepository.AddAsync(Arg.Any<UserAchievement>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                achievements.Add(call.ArgAt<UserAchievement>(0));
                return Task.CompletedTask;
            });
        foundingReader.ReadEvidenceAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new FoundingAchievementEvidence(true, false, false, false, false));
        accountResetRepository.DeleteAllUserDataAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                achievements.Clear();
                return Task.CompletedTask;
            });
        userDateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 8, 23));

        var gamificationService = new GamificationService(
            new GamificationRepositories(
                userRepository,
                Substitute.For<IGenericRepository<Habit>>(),
                Substitute.For<IGenericRepository<HabitLog>>(),
                Substitute.For<IGenericRepository<Goal>>(),
                achievementRepository,
                Substitute.For<IGenericRepository<Notification>>(),
                xpAwardLogRepository,
                foundingReader),
            new GamificationNotifiers(
                Substitute.For<IPushNotificationService>(),
                Substitute.For<IFriendFeedEventEmitter>()),
            userDateService,
            new XpAwarder(xpAwardLogRepository),
            unitOfWork,
            featureFlagService,
            Substitute.For<ILogger<GamificationService>>());
        var resetHandler = new ResetAccountCommandHandler(
            userRepository,
            accountResetRepository,
            unitOfWork,
            userDateService,
            new MemoryCache(new MemoryCacheOptions()));

        var reconciliationTask = gamificationService.ReconcileFoundingAchievementsAsync(userId);
        await evidenceSelected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var resetTask = resetHandler.Handle(new ResetAccountCommand(userId), CancellationToken.None);
        await unitOfWork.SecondLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        resetTask.IsCompleted.Should().BeFalse();

        allowGrant.TrySetResult(true);
        var granted = await reconciliationTask;
        var resetResult = await resetTask;

        granted.Should().ContainSingle().Which.Should().Be(AchievementDefinitions.Liftoff);
        resetResult.IsSuccess.Should().BeTrue();
        achievements.Should().BeEmpty();
        user.TotalXp.Should().Be(0);
    }

    private sealed class BlockingFeatureFlagService(
        TaskCompletionSource<bool> evidenceSelected,
        TaskCompletionSource<bool> allowGrant) : IFeatureFlagService
    {
        private int _readCount;

        public async Task<IReadOnlyList<string>> GetEnabledKeysForUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 2)
            {
                evidenceSelected.TrySetResult(true);
                await allowGrant.Task.WaitAsync(cancellationToken);
            }

            return [FeatureFlagKeys.GamificationFreeTier];
        }
    }

    private sealed class SerializingUnitOfWork : IUnitOfWork
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly AsyncLocal<TransactionState?> _transaction = new();
        private int _lockRequestCount;

        public TaskCompletionSource<bool> SecondLockRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteInTransactionAsync(async token =>
            {
                await operation(token);
                return true;
            }, cancellationToken);

        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            var previous = _transaction.Value;
            var current = new TransactionState();
            _transaction.Value = current;
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                current.HeldLock?.Release();
                _transaction.Value = previous;
            }
        }

        public async Task AcquireAdvisoryLockAsync(string key, CancellationToken cancellationToken = default)
        {
            var transaction = _transaction.Value
                ?? throw new InvalidOperationException("A transaction is required for an advisory lock.");
            if (Interlocked.Increment(ref _lockRequestCount) == 2)
                SecondLockRequested.TrySetResult(true);

            var accountLock = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await accountLock.WaitAsync(cancellationToken);
            transaction.HeldLock = accountLock;
        }

        public void DiscardChanges()
        {
        }

        public void ResetTracking()
        {
        }

        public void Dispose()
        {
        }

        private sealed class TransactionState
        {
            public SemaphoreSlim? HeldLock { get; set; }
        }
    }
}
