using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Queries.Gamification;

public class ClosedMonthRecapResetRaceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly MonthStart = new(2026, 6, 1);
    private static readonly DateOnly MonthEnd = new(2026, 6, 30);

    [Fact]
    public async Task RecapCreationInProgress_ThenReset_RemovesTheCreatedSnapshot()
    {
        var harness = new RaceHarness();
        var sourcesRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSnapshotCreation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeGoalCountAsync = async cancellationToken =>
        {
            sourcesRead.TrySetResult(true);
            await allowSnapshotCreation.Task.WaitAsync(cancellationToken);
        };

        var recapTask = harness.RecapHandler.Handle(CreateRecapQuery(), CancellationToken.None);
        await sourcesRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var resetTask = harness.ResetHandler.Handle(new ResetAccountCommand(UserId), CancellationToken.None);
        var lockWait = await Task.WhenAny(
            harness.UnitOfWork.SecondLockRequested.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));

        allowSnapshotCreation.TrySetResult(true);
        await Task.WhenAll(recapTask, resetTask);

        lockWait.Should().BeSameAs(harness.UnitOfWork.SecondLockRequested.Task);
        (await recapTask).IsSuccess.Should().BeTrue();
        (await resetTask).IsSuccess.Should().BeTrue();
        harness.StoredResponseJson.Should().BeNull();
    }

    [Fact]
    public async Task ResetCompletesBeforeRecapLock_RecapReadsOnlyPostResetData()
    {
        var harness = new RaceHarness();
        var initialProbeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowInitialProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeInitialStoreProbeAsync = async cancellationToken =>
        {
            initialProbeStarted.TrySetResult(true);
            await allowInitialProbe.Task.WaitAsync(cancellationToken);
        };

        var recapTask = harness.RecapHandler.Handle(CreateRecapQuery(), CancellationToken.None);
        await initialProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var resetResult = await harness.ResetHandler.Handle(new ResetAccountCommand(UserId), CancellationToken.None);
        allowInitialProbe.TrySetResult(true);
        var recapResult = await recapTask;

        resetResult.IsSuccess.Should().BeTrue();
        recapResult.IsSuccess.Should().BeTrue();
        recapResult.Value.Metrics.TotalCompletions.Should().Be(0);
        harness.StoredResponseJson.Should().NotBeNull();
        JsonSerializer.Deserialize<RecapResponse>(harness.StoredResponseJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Metrics.TotalCompletions.Should().Be(0);
    }

    private static GetRecapQuery CreateRecapQuery() =>
        new(UserId, MonthStart, MonthEnd, "month", 2026, 6);

    private sealed class RaceHarness
    {
        private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
        private readonly IGenericRepository<Goal> _goalRepository = Substitute.For<IGenericRepository<Goal>>();
        private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
        private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
        private readonly IMediator _mediator = Substitute.For<IMediator>();
        private readonly IClosedMonthRecapStore _recapStore = Substitute.For<IClosedMonthRecapStore>();
        private readonly IAccountResetRepository _accountResetRepository = Substitute.For<IAccountResetRepository>();
        private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
        private readonly List<Habit> _habits;
        private int _storeProbeCount;

        public RaceHarness()
        {
            UnitOfWork = new SerializingUnitOfWork();
            var user = User.Create("Recap User", $"recap-race-{Guid.NewGuid():N}@example.com").Value;
            user.SetTimeZone("UTC").IsSuccess.Should().BeTrue();
            typeof(User).GetProperty(nameof(User.CreatedAtUtc))!.SetValue(
                user,
                new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var habit = Habit.Create(new HabitCreateParams(
                UserId,
                "Walk",
                FrequencyUnit.Day,
                1,
                DueDate: MonthStart)).Value;
            typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
                habit,
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
            habit.Log(MonthStart, advanceDueDate: false);
            _habits = [habit];

            _userRepository.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
            _userRepository.FindOneTrackedAsync(
                    Arg.Any<Expression<Func<User, bool>>>(),
                    Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(user);
            _habitRepository.FindIgnoringFiltersAsync(
                    Arg.Any<Expression<Func<Habit, bool>>>(),
                    Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => _habits.ToList());
            _goalRepository.CountAsync(
                    Arg.Any<Expression<Func<Goal, bool>>>(),
                    Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (BeforeGoalCountAsync is not null)
                        await BeforeGoalCountAsync(call.ArgAt<CancellationToken>(1));
                    return 0;
                });
            _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success("ABCD2345"));
            _recapStore.FindResponseJsonAsync(
                    UserId,
                    MonthStart,
                    MonthEnd,
                    Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (Interlocked.Increment(ref _storeProbeCount) == 1 && BeforeInitialStoreProbeAsync is not null)
                        await BeforeInitialStoreProbeAsync(call.ArgAt<CancellationToken>(3));
                    return StoredResponseJson;
                });
            _recapStore.AddAsync(Arg.Any<ClosedMonthRecap>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    StoredResponseJson = call.ArgAt<ClosedMonthRecap>(0).ResponseJson;
                    return Task.CompletedTask;
                });
            _accountResetRepository.DeleteAllUserDataAsync(UserId, Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    _habits.Clear();
                    StoredResponseJson = null;
                    return Task.CompletedTask;
                });
            _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
                .Returns(new DateOnly(2026, 8, 23));

            RecapHandler = new GetRecapQueryHandler(
                _habitRepository,
                _goalRepository,
                _userRepository,
                _userStreakService,
                Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" }),
                _mediator,
                _recapStore,
                UnitOfWork);
            ResetHandler = new ResetAccountCommandHandler(
                _userRepository,
                _accountResetRepository,
                UnitOfWork,
                _userDateService,
                new MemoryCache(new MemoryCacheOptions()));
        }

        public GetRecapQueryHandler RecapHandler { get; }
        public ResetAccountCommandHandler ResetHandler { get; }
        public SerializingUnitOfWork UnitOfWork { get; }
        public string? StoredResponseJson { get; private set; }
        public Func<CancellationToken, Task>? BeforeGoalCountAsync { get; set; }
        public Func<CancellationToken, Task>? BeforeInitialStoreProbeAsync { get; set; }
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
