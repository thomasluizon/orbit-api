using System.Collections.Concurrent;
using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

public class HabitCeilingLockTests
{
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

        public async Task AcquireAdvisoryLockAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            var transaction = _transaction.Value
                ?? throw new InvalidOperationException("A transaction is required for an advisory lock.");
            if (Interlocked.Increment(ref _lockRequestCount) == 2)
                SecondLockRequested.TrySetResult(true);

            var ceilingLock = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await ceilingLock.WaitAsync(cancellationToken);
            transaction.HeldLock = ceilingLock;
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
