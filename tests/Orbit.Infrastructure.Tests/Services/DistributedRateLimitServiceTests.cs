using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class DistributedRateLimitServiceTests : IDisposable
{
    private static readonly DateTimeOffset MidWindowInstant =
        new(2026, 1, 15, 12, 30, 30, TimeSpan.Zero);

    private readonly OrbitDbContext _dbContext;
    private readonly DistributedRateLimitService _service;

    public DistributedRateLimitServiceTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DistributedRateLimitServiceTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        _service = new DistributedRateLimitService(_dbContext, new FixedTimeProvider(MidWindowInstant));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TryAcquireAsync_AuthPolicy_BlocksAfterPermitLimit()
    {
        DistributedRateLimitDecision finalDecision = new(true, 0, 0, DateTime.UtcNow);

        for (var attempt = 0; attempt < 11; attempt++)
            finalDecision = await _service.TryAcquireAsync("auth", "ip:127.0.0.1");

        finalDecision.Allowed.Should().BeFalse();
        finalDecision.PermitLimit.Should().Be(10);
        finalDecision.CurrentCount.Should().Be(10);
    }

    [Fact]
    public async Task TryAcquireAsync_ChatPolicy_UsesIndependentPartitions()
    {
        for (var attempt = 0; attempt < 20; attempt++)
            (await _service.TryAcquireAsync("chat", "user:one")).Allowed.Should().BeTrue();

        var blocked = await _service.TryAcquireAsync("chat", "user:one");
        var otherPartition = await _service.TryAcquireAsync("chat", "user:two");

        blocked.Allowed.Should().BeFalse();
        blocked.PermitLimit.Should().Be(20);
        otherPartition.Allowed.Should().BeTrue();
        otherPartition.CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task UploadReads_AreNotAStoredRateLimitPolicy()
    {
        var acquire = () => _service.TryAcquireAsync("upload-reads", "ip:203.0.113.7");

        await acquire.Should().ThrowAsync<InvalidOperationException>();
        _dbContext.DistributedRateLimitBuckets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("challenges")]
    [InlineData("accountability-invites")]
    [InlineData("accountability-checkins")]
    [InlineData("accountability-mutations")]
    [InlineData("cheers")]
    [InlineData("friend-requests")]
    [InlineData("friend-mutations")]
    [InlineData("invite-preview")]
    [InlineData("reports")]
    [InlineData("set-handle")]
    [InlineData("block")]
    [InlineData("unblock")]
    [InlineData("public-profile")]
    [InlineData("unknown-policy")]
    public async Task TryAcquireAsync_RemovedOrUnknownPolicy_ThrowsWithoutStoringBucket(string policyName)
    {
        var acquire = () => _service.TryAcquireAsync(policyName, "user:one");

        await acquire.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Unknown rate-limit policy '{policyName}'.");
        _dbContext.DistributedRateLimitBuckets.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatLease_AcquireRefuseRelease_AllowsUsersIndependently()
    {
        using var scope = new RelationalRateLimitScope(new FixedTimeProvider(MidWindowInstant));
        var service = scope.Service;
        var leaseDuration = TimeSpan.FromMinutes(5);

        var firstUser = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration);
        var blockedFirstUser = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration);
        var secondUser = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:B",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration);

        firstUser.Allowed.Should().BeTrue();
        blockedFirstUser.Allowed.Should().BeFalse();
        secondUser.Allowed.Should().BeTrue();

        await service.ReleaseLeaseAsync(
            "chat-in-flight",
            "user:A",
            firstUser.WindowEndsAtUtc);
        var reacquired = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration);

        reacquired.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ChatLease_StaleRelease_DoesNotDeleteSuccessorLease()
    {
        var clock = new MutableTimeProvider(MidWindowInstant);
        using var scope = new RelationalRateLimitScope(clock);
        var service = scope.Service;
        var leaseDuration = TimeSpan.FromMinutes(5);
        var expiredLease = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration);
        clock.Advance(leaseDuration.Add(TimeSpan.FromSeconds(1)));
        var successorLease = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration);

        await service.ReleaseLeaseAsync(
            "chat-in-flight",
            "user:A",
            expiredLease.WindowEndsAtUtc);
        var blockedBySuccessor = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration);

        successorLease.Allowed.Should().BeTrue();
        blockedBySuccessor.Allowed.Should().BeFalse();
        blockedBySuccessor.WindowEndsAtUtc.Should().Be(successorLease.WindowEndsAtUtc);
    }

    [Fact]
    public async Task ChatLease_ExpiredWithoutRelease_IsReclaimed()
    {
        var clock = new MutableTimeProvider(MidWindowInstant);
        using var scope = new RelationalRateLimitScope(clock);
        var service = scope.Service;
        var leaseDuration = TimeSpan.FromMinutes(5);

        (await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration)).Allowed.Should().BeTrue();
        clock.Advance(leaseDuration.Add(TimeSpan.FromSeconds(1)));

        var reacquired = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            leaseDuration);

        reacquired.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ChatLease_Release_DoesNotSaveUnrelatedTrackedChanges()
    {
        using var scope = new RelationalRateLimitScope(new FixedTimeProvider(MidWindowInstant));
        var service = scope.Service;
        var lease = await service.TryAcquireLeaseAsync(
            "chat-in-flight",
            "user:A",
            AppConstants.MaxConcurrentChatRequestsPerUser,
            TimeSpan.FromMinutes(5));
        var pendingBucket = DistributedRateLimitBucket.Create(
            "chat",
            "user:pending",
            MidWindowInstant.UtcDateTime,
            MidWindowInstant.UtcDateTime.AddMinutes(1));
        scope.Context.DistributedRateLimitBuckets.Add(pendingBucket);

        await service.ReleaseLeaseAsync(
            "chat-in-flight",
            "user:A",
            lease.WindowEndsAtUtc);

        scope.Context.Entry(pendingBucket).State.Should().Be(EntityState.Added);
        (await scope.Context.DistributedRateLimitBuckets
            .AsNoTracking()
            .AnyAsync(bucket => bucket.Id == pendingBucket.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquireAsync_RelationalProvider_UsesExecutionStrategyTransactionPath()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new RateLimitOnlyOrbitDbContext(options);
        context.Database.EnsureCreated();
        var service = new DistributedRateLimitService(context, new FixedTimeProvider(MidWindowInstant));

        DistributedRateLimitDecision decision = new(true, 0, 0, DateTime.UtcNow);

        for (var attempt = 0; attempt < 11; attempt++)
            decision = await service.TryAcquireAsync("auth", "ip:203.0.113.10");

        decision.Allowed.Should().BeFalse();
        decision.PermitLimit.Should().Be(10);
        decision.CurrentCount.Should().Be(10);
    }

    [Theory]
    [InlineData("tags", 60)]
    [InlineData("achievements", 30)]
    [InlineData("ai-operations", 15)]
    [InlineData("streak-repair", 10)]
    public async Task TryAcquireAsync_StateChangingSweepPolicy_BlocksAfterPermitLimit(string policyName, int permitLimit)
    {
        DistributedRateLimitDecision finalDecision = new(true, 0, 0, DateTime.UtcNow);

        for (var attempt = 0; attempt <= permitLimit; attempt++)
            finalDecision = await _service.TryAcquireAsync(policyName, "user:sweep");

        finalDecision.Allowed.Should().BeFalse();
        finalDecision.PermitLimit.Should().Be(permitLimit);
        finalDecision.CurrentCount.Should().Be(permitLimit);
    }

    [Fact]
    public async Task TryAcquireAsync_TagsPolicy_PartitionsPerUser()
    {
        for (var attempt = 0; attempt < 60; attempt++)
            (await _service.TryAcquireAsync("tags", "user:one")).Allowed.Should().BeTrue();

        var exhausted = await _service.TryAcquireAsync("tags", "user:one");
        var otherUser = await _service.TryAcquireAsync("tags", "user:two");

        exhausted.Allowed.Should().BeFalse();
        otherUser.Allowed.Should().BeTrue();
        otherUser.CurrentCount.Should().Be(1);
    }

    [Theory]
    [InlineData("mcp", 60)]
    [InlineData("mcp-ai", 15)]
    public async Task TryAcquireAsync_McpPolicies_BlockAfterPermitLimit(string policyName, int permitLimit)
    {
        DistributedRateLimitDecision finalDecision = new(true, 0, 0, DateTime.UtcNow);

        for (var attempt = 0; attempt <= permitLimit; attempt++)
            finalDecision = await _service.TryAcquireAsync(policyName, "api-key:one");

        finalDecision.Allowed.Should().BeFalse();
        finalDecision.PermitLimit.Should().Be(permitLimit);
        finalDecision.CurrentCount.Should().Be(permitLimit);
    }

    [Fact]
    public async Task TryAcquireAsync_McpAiPolicy_RemainsExhaustedAcrossSegmentBoundary()
    {
        var clock = new MutableTimeProvider(MidWindowInstant);
        var service = new DistributedRateLimitService(_dbContext, clock);

        for (var attempt = 0; attempt < 15; attempt++)
            (await service.TryAcquireAsync("mcp-ai", "api-key:one")).Allowed.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(15));
        var blocked = await service.TryAcquireAsync("mcp-ai", "api-key:one");

        blocked.Allowed.Should().BeFalse();
        blocked.CurrentCount.Should().Be(15);
        blocked.WindowEndsAtUtc.Should().Be(MidWindowInstant.UtcDateTime.AddMinutes(1));
    }

    [Theory]
    [InlineData("40001", false, false)]
    [InlineData("40P01", false, false)]
    [InlineData("23505", false, false)]
    [InlineData("40001", true, true)]
    [InlineData("40P01", true, true)]
    [InlineData("23505", true, true)]
    public async Task TryAcquireAsync_ActualStrategy_AbortedSavesCommitOnePermit(
        string sqlState, bool wrapped, bool warm)
    {
        using var scope = new ProviderRateLimitScope();
        if (warm)
            await scope.Service.TryAcquireAsync("auth", "user:retry");
        scope.Save.Reset(attempt => attempt <= 2 ? Conflict(sqlState, wrapped) : null);
        scope.Transactions.Reset();

        var decision = await scope.Service.TryAcquireAsync("auth", "user:retry");

        decision.Should().Be(new DistributedRateLimitDecision(true, 10, warm ? 2 : 1,
            MidWindowInstant.UtcDateTime.AddSeconds(30)));
        scope.Save.Attempts.Should().Be(3);
        scope.Transactions.Starts.Should().Be(3);
        scope.Transactions.Commits.Should().Be(1);
        (await scope.Context.DistributedRateLimitBuckets.AsNoTracking().SingleAsync()).Count.Should().Be(warm ? 2 : 1);
    }

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("23505")]
    [InlineData("mixed")]
    [InlineData("transport")]
    public async Task TryAcquireAsync_ActualStrategy_ExhaustionHasThreeAttemptsAndRetainsCause(string failure)
    {
        using var scope = new ProviderRateLimitScope();
        scope.Save.Reset(attempt => failure switch
        {
            "mixed" => Conflict(new[] { "23505", "40001", "40P01" }[(attempt - 1) % 3]),
            "transport" => new NpgsqlException("Connection interrupted before commit.", new IOException()),
            _ => Conflict(failure)
        });
        var acquire = () => scope.Service.TryAcquireAsync("auth", "user:retry");

        var exception = await acquire.Should().ThrowAsync<Exception>();

        scope.Save.Attempts.Should().Be(3);
        scope.Transactions.Starts.Should().Be(3);
        scope.Transactions.Commits.Should().Be(0);
        exception.Which.Should().BeOfType<RetryLimitExceededException>()
            .Which.InnerException.Should().BeSameAs(scope.Save.LastFailure);
        (await scope.Context.DistributedRateLimitBuckets.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("23505")]
    public async Task TryAcquireAsync_ActualStrategy_ConfirmedCommitAbortRetries(string sqlState)
    {
        using var scope = new ProviderRateLimitScope();
        scope.Transactions.BeforeCommit = attempt => attempt <= 2 ? Conflict(sqlState) : null;

        var result = await scope.Service.TryAcquireAsync("auth", "user:commit");

        result.Allowed.Should().BeTrue();
        result.CurrentCount.Should().Be(1);
        scope.Transactions.Starts.Should().Be(3);
        scope.Transactions.CommitAttempts.Should().Be(3);
        scope.Transactions.Commits.Should().Be(1);
        (await scope.Context.DistributedRateLimitBuckets.AsNoTracking().SingleAsync()).Count.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryAcquireAsync_ActualStrategy_UnknownCommitOutcomeIsNeverReplayed(bool committed)
    {
        using var scope = new ProviderRateLimitScope();
        var failure = new NpgsqlException("Commit acknowledgement lost.", new IOException());
        if (committed)
            scope.Transactions.AfterCommit = _ => failure;
        else
            scope.Transactions.BeforeCommit = _ => failure;
        var acquire = () => scope.Service.TryAcquireAsync("auth", "user:commit");

        var exception = await acquire.Should().ThrowAsync<Exception>();

        scope.Transactions.Starts.Should().Be(1);
        scope.Save.Attempts.Should().Be(1);
        exception.Which.Should().BeSameAs(failure);
        (await scope.Context.DistributedRateLimitBuckets.AsNoTracking().SumAsync(bucket => bucket.Count))
            .Should().Be(committed ? 1 : 0);
    }

    [Theory]
    [InlineData("23503")]
    [InlineData("unrelated")]
    [InlineData("terminal-wrapper")]
    [InlineData("null-inner")]
    public async Task TryAcquireAsync_ActualStrategy_UnrelatedFailuresTerminateOnce(string kind)
    {
        using var scope = new ProviderRateLimitScope();
        Exception failure = kind switch
        {
            "23503" => Conflict(kind),
            "terminal-wrapper" => new RetryLimitExceededException("Already exhausted.", Conflict("40001")),
            "null-inner" => new DbUpdateException("No inner exception."),
            _ => new InvalidOperationException("Unrelated failure.")
        };
        scope.Save.Reset(_ => failure);
        var acquire = () => scope.Service.TryAcquireAsync("auth", "user:failure");

        var exception = await acquire.Should().ThrowAsync<Exception>();

        exception.Which.Should().BeSameAs(failure);
        scope.Save.Attempts.Should().Be(1);
        scope.Transactions.Starts.Should().Be(1);
        (await scope.Context.DistributedRateLimitBuckets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TryAcquireAsync_ActualStrategy_PreCanceledNeverStartsTransaction()
    {
        using var scope = new ProviderRateLimitScope();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var acquire = () => scope.Service.TryAcquireAsync("auth", "user:canceled", cancellation.Token);

        await acquire.Should().ThrowAsync<OperationCanceledException>();

        scope.Transactions.Starts.Should().Be(0);
        scope.Save.Attempts.Should().Be(0);
        (await scope.Context.DistributedRateLimitBuckets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TryAcquireAsync_PendingBusinessMutation_IsNeitherSavedNorCleared()
    {
        using var scope = new ProviderRateLimitScope();
        var user = User.Create("Pending", "pending@example.com").Value;
        scope.Context.Users.Add(user);
        var acquire = () => scope.Service.TryAcquireAsync("auth", "user:pending");

        await acquire.Should().ThrowAsync<InvalidOperationException>().WithMessage("*pending changes*");

        scope.Transactions.Starts.Should().Be(0);
        scope.Save.Attempts.Should().Be(0);
        scope.Context.Entry(user).State.Should().Be(EntityState.Added);
        (await scope.Context.Users.AsNoTracking().AnyAsync(item => item.Id == user.Id)).Should().BeFalse();
        (await scope.Context.DistributedRateLimitBuckets.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("auth", 10, 60)]
    [InlineData("chat", 20, 15)]
    public async Task TryAcquireAsync_ActualStrategy_WindowRolloverAndExactExpiryPreserveQuota(
        string policy, int limit, int segmentSeconds)
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero));
        using var scope = new ProviderRateLimitScope(clock);
        for (var i = 0; i < limit; i++)
            (await scope.Service.TryAcquireAsync(policy, "user:window")).Allowed.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(segmentSeconds));

        var rollover = await scope.Service.TryAcquireAsync(policy, "user:window");

        rollover.Allowed.Should().Be(policy == "auth");
        rollover.CurrentCount.Should().Be(policy == "auth" ? 1 : limit);
        if (policy == "chat")
        {
            rollover.WindowEndsAtUtc.Should().Be(new DateTime(2026, 1, 15, 12, 31, 0, DateTimeKind.Utc));
            (await scope.Context.DistributedRateLimitBuckets.AsNoTracking().SumAsync(bucket => bucket.Count)).Should().Be(limit);
            clock.Advance(TimeSpan.FromSeconds(45));
            var expired = await scope.Service.TryAcquireAsync(policy, "user:window");
            expired.Allowed.Should().BeTrue();
            expired.CurrentCount.Should().Be(1);
        }
        (await scope.Context.DistributedRateLimitBuckets.AsNoTracking().SingleAsync()).Count.Should().Be(1);
    }

    [Fact]
    public async Task TryAcquireAsync_ActualStrategy_RetryWaitUsesCallerTokenAndStopsBeforeNextAttempt()
    {
        using var scope = new ProviderRateLimitScope();
        using var cancellation = new CancellationTokenSource();
        var strategy = new ObservedAcquisitionStrategy(scope.Context)
        {
            OnWaiting = cancellation.Cancel
        };
        var service = new ObservedRateLimitService(scope.Context, strategy);
        scope.Save.Reset(_ => Conflict("40001"));
        var acquire = () => service.TryAcquireAsync("auth", "user:wait", cancellation.Token);

        await acquire.Should().ThrowAsync<OperationCanceledException>();

        strategy.CallerToken.Should().Be(cancellation.Token);
        strategy.Waits.Should().Be(2);
        strategy.MaxRetryCount.Should().Be(2);
        strategy.MaxRetryDelay.Should().Be(TimeSpan.FromSeconds(5));
        scope.Save.Attempts.Should().Be(2);
        scope.Transactions.Starts.Should().Be(2);
        scope.Transactions.Commits.Should().Be(0);
        scope.Save.Tokens.Should().OnlyContain(token => token == cancellation.Token);
        scope.Transactions.Tokens.Should().OnlyContain(token => token == cancellation.Token);
        (await scope.Context.DistributedRateLimitBuckets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TryAcquireAsync_ActualStrategy_SuccessForwardsTokenThroughCommit()
    {
        using var scope = new ProviderRateLimitScope();
        using var cancellation = new CancellationTokenSource();

        var decision = await scope.Service.TryAcquireAsync("auth", "user:token", cancellation.Token);

        decision.Allowed.Should().BeTrue();
        scope.Save.Tokens.Should().ContainSingle().Which.Should().Be(cancellation.Token);
        scope.Transactions.Tokens.Should().HaveCount(2).And.OnlyContain(token => token == cancellation.Token);
        var strategy = scope.Save.Strategies.Single();
        strategy.Should().BeAssignableTo<NpgsqlRetryingExecutionStrategy>();
        strategy!.MaxRetryCount.Should().Be(2);
        strategy.MaxRetryDelay.Should().Be(TimeSpan.FromSeconds(5));
    }

    private sealed class ObservedRateLimitService(
        OrbitDbContext context, DistributedRateLimitService.AcquisitionExecutionStrategy strategy)
        : DistributedRateLimitService(context, new FixedTimeProvider(MidWindowInstant))
    {
        internal override AcquisitionExecutionStrategy CreateAcquisitionStrategy() => strategy;
    }

    private sealed class ObservedAcquisitionStrategy(OrbitDbContext context)
        : DistributedRateLimitService.AcquisitionExecutionStrategy(context)
    {
        public CancellationToken CallerToken { get; private set; }
        public int Waits { get; private set; }
        public Action OnWaiting { get; init; } = () => { };

        public override Task<TResult> ExecuteAsync<TState, TResult>(TState state,
            Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
            Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
            CancellationToken cancellationToken = default)
        {
            CallerToken = cancellationToken;
            return base.ExecuteAsync(state, operation, verifySucceeded, cancellationToken);
        }

        protected override void OnRetry()
        {
            Waits++;
            if (Waits == 2)
                OnWaiting();
            base.OnRetry();
        }
    }

    private static Exception Conflict(string sqlState, bool wrapped = true)
    {
        var postgres = new PostgresException("Transaction aborted.", "ERROR", "ERROR", sqlState);
        return wrapped ? new DbUpdateException("Aborted save.", postgres) : postgres;
    }

    private sealed class ProviderRateLimitScope : IDisposable
    {
        private readonly SqliteOrbitDbContextFactory _factory;

        public ProviderRateLimitScope(TimeProvider? clock = null)
        {
            _factory = new SqliteOrbitDbContextFactory(
                dependencies => new NpgsqlRetryingExecutionStrategy(dependencies, 3, TimeSpan.Zero, null),
                Save, Transactions);
            Transactions.Enabled = true;
            Service = new DistributedRateLimitService(Context, clock ?? new FixedTimeProvider(MidWindowInstant));
        }

        public SaveFailure Save { get; } = new();
        public TransactionFailure Transactions { get; } = new();
        public OrbitDbContext Context => _factory.Context;
        public DistributedRateLimitService Service { get; }
        public void Dispose() => _factory.Dispose();
    }

    private sealed class SaveFailure : SaveChangesInterceptor
    {
        private Func<int, Exception?> _failure = _ => null;
        public int Attempts { get; private set; }
        public Exception? LastFailure { get; private set; }
        public List<CancellationToken> Tokens { get; } = [];
        public List<ExecutionStrategy?> Strategies { get; } = [];

        public void Reset(Func<int, Exception?> failure)
        {
            Attempts = 0;
            _failure = failure;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            Tokens.Add(cancellationToken);
            Strategies.Add(ExecutionStrategy.Current);
            LastFailure = _failure(Attempts);
            if (LastFailure is not null)
                throw LastFailure;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TransactionFailure : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public int Starts { get; private set; }
        public int CommitAttempts { get; private set; }
        public int Commits { get; private set; }
        public Func<int, Exception?> BeforeCommit { get; set; } = _ => null;
        public Func<int, Exception?> AfterCommit { get; set; } = _ => null;
        public List<CancellationToken> Tokens { get; } = [];

        public void Reset()
        {
            Starts = 0;
            CommitAttempts = 0;
            Commits = 0;
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Starts++;
                Tokens.Add(cancellationToken);
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                CommitAttempts++;
                transaction.IsolationLevel.Should().Be(IsolationLevel.Serializable);
                Tokens.Add(cancellationToken);
                var failure = BeforeCommit(CommitAttempts);
                if (failure is not null)
                    throw failure;
            }
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Commits++;
                var failure = AfterCommit(Commits);
                if (failure is not null)
                    throw failure;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class MutableTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        private DateTimeOffset _instant = instant;

        public override DateTimeOffset GetUtcNow() => _instant;

        public void Advance(TimeSpan duration) => _instant = _instant.Add(duration);
    }

    private sealed class RelationalRateLimitScope : IDisposable
    {
        private readonly SqliteConnection _connection;

        public RelationalRateLimitScope(TimeProvider clock)
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<OrbitDbContext>()
                .UseSqlite(_connection)
                .Options;
            Context = new RateLimitOnlyOrbitDbContext(options);
            Context.Database.EnsureCreated();
            Service = new DistributedRateLimitService(Context, clock);
        }

        public RateLimitOnlyOrbitDbContext Context { get; }

        public DistributedRateLimitService Service { get; }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }

    private class RateLimitOnlyOrbitDbContext(DbContextOptions<OrbitDbContext> options)
        : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<User>();
            modelBuilder.Ignore<Habit>();
            modelBuilder.Ignore<HabitLog>();
            modelBuilder.Ignore<AppConfig>();
            modelBuilder.Ignore<Tag>();
            modelBuilder.Ignore<PushSubscription>();
            modelBuilder.Ignore<SentReminder>();
            modelBuilder.Ignore<SentSlipAlert>();
            modelBuilder.Ignore<Notification>();
            modelBuilder.Ignore<Goal>();
            modelBuilder.Ignore<GoalProgressLog>();
            modelBuilder.Ignore<Referral>();
            modelBuilder.Ignore<UserAchievement>();
            modelBuilder.Ignore<StreakFreeze>();
            modelBuilder.Ignore<UserSession>();
            modelBuilder.Ignore<ApiKey>();
            modelBuilder.Ignore<PendingAgentOperationState>();
            modelBuilder.Ignore<AgentStepUpChallengeState>();
            modelBuilder.Ignore<AgentAuditLog>();
            modelBuilder.Ignore<ChecklistTemplate>();
            modelBuilder.Ignore<AppFeatureFlag>();
            modelBuilder.Ignore<AppFeatureFlagAccount>();
            modelBuilder.Ignore<ContentBlock>();
            modelBuilder.Ignore<GoogleCalendarSyncSuggestion>();

            modelBuilder.Entity<DistributedRateLimitBucket>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.HasIndex(item => new { item.PolicyName, item.PartitionKey, item.WindowStartUtc }).IsUnique();
                entity.Property(item => item.PolicyName).HasMaxLength(64);
                entity.Property(item => item.PartitionKey).HasMaxLength(256);
            });
        }
    }

}
