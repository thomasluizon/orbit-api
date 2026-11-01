using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class DistributedRateLimitServiceTests
{
    [Fact]
    public async Task TryAcquireAsync_Coordination_PrecedesAllBucketCommandsInsideSerializableTransaction()
    {
        var commands = new AcquisitionCommands();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        commands.Enabled = true;
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var service = new CoordinatedService(factory.Context, token =>
        {
            calls++;
            token.CanBeCanceled.Should().BeTrue();
            token.IsCancellationRequested.Should().BeFalse();
            factory.Context.Database.CurrentTransaction.Should().NotBeNull();
            factory.Context.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel.Should().Be(IsolationLevel.Serializable);
            commands.Count.Should().Be(0);
            return Task.CompletedTask;
        });

        var decision = await service.TryAcquireAsync("auth", "user:ordered", cancellation.Token);

        decision.Allowed.Should().BeTrue();
        calls.Should().Be(1);
        commands.Count.Should().BeGreaterThan(0);
        factory.Context.Database.CurrentTransaction.Should().BeNull();
        (await factory.Context.DistributedRateLimitBuckets.AsNoTracking().SingleAsync()).Count.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryAcquireAsync_Coordination_ErrorDisposesTransactionWithoutRunningCore(bool transient)
    {
        var commands = new AcquisitionCommands();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        commands.Enabled = true;
        var calls = 0;
        var transactions = new List<DbTransaction>();
        var failure = transient ? Conflict("40001") : new InvalidOperationException("Lock rejected.");
        var service = new CoordinatedService(factory.Context, _ =>
        {
            calls++;
            transactions.Add(factory.Context.Database.CurrentTransaction!.GetDbTransaction());
            throw failure;
        });

        var acquire = () => service.TryAcquireAsync("auth", "user:lock-failure");
        var thrown = await acquire.Should().ThrowAsync<Exception>();

        if (transient)
            thrown.Which.Should().BeOfType<RetryLimitExceededException>().Which.InnerException.Should().BeSameAs(failure);
        else
            thrown.Which.Should().BeSameAs(failure);
        calls.Should().Be(transient ? 3 : 1);
        commands.Count.Should().Be(0);
        factory.Context.Database.CurrentTransaction.Should().BeNull();
        transactions.Should().OnlyContain(transaction => transaction.Connection == null);
        factory.Context.ChangeTracker.HasChanges().Should().BeFalse();
        (await new DistributedRateLimitService(factory.Context, new FixedTimeProvider(MidWindowInstant))
            .TryAcquireAsync("auth", "user:after-failure")).CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task TryAcquireAsync_Coordination_CanceledWaitDisposesTransactionWithoutRetryOrSave()
    {
        var commands = new AcquisitionCommands();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        commands.Enabled = true;
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        DbTransaction? transaction = null;
        var service = new CoordinatedService(factory.Context, async token =>
        {
            calls++;
            token.CanBeCanceled.Should().BeTrue();
            token.IsCancellationRequested.Should().BeFalse();
            transaction = factory.Context.Database.CurrentTransaction!.GetDbTransaction();
            waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var acquisition = service.TryAcquireAsync("auth", "user:lock-canceled", cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var complete = () => acquisition.WaitAsync(TimeSpan.FromSeconds(10));
        await complete.Should().ThrowAsync<OperationCanceledException>();

        calls.Should().Be(1);
        commands.Count.Should().Be(0);
        factory.Context.Database.CurrentTransaction.Should().BeNull();
        transaction.Should().NotBeNull();
        transaction!.Connection.Should().BeNull();
        factory.Context.ChangeTracker.Entries<DistributedRateLimitBucket>().Should().BeEmpty();
        (await new DistributedRateLimitService(factory.Context, new FixedTimeProvider(MidWindowInstant))
            .TryAcquireAsync("auth", "user:after-cancel")).CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task TryAcquireAsync_Coordination_ServerDeadlineRollsBackAndRestoresCompletionToken()
    {
        var commands = new AcquisitionCommands();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        commands.Enabled = true;
        using var previous = new CancellationTokenSource();
        factory.Context.TransactionCompletionToken = previous.Token;
        DbTransaction? transaction = null;
        var service = new CoordinatedService(factory.Context, async token =>
        {
            transaction = factory.Context.Database.CurrentTransaction!.GetDbTransaction();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, new DatabaseConnectionSettings { TransactionTimeoutSeconds = 1 });
        var acquire = () => service.TryAcquireAsync("auth", "user:deadline");

        await acquire.Should().ThrowAsync<TimeoutException>()
            .WithMessage("Transaction exceeded the 1s timeout and was rolled back.");

        commands.Count.Should().Be(0);
        factory.Context.TransactionCompletionToken.Should().Be(previous.Token);
        factory.Context.Database.CurrentTransaction.Should().BeNull();
        transaction.Should().NotBeNull();
        transaction!.Connection.Should().BeNull();
        factory.Context.ChangeTracker.Entries<DistributedRateLimitBucket>().Should().BeEmpty();
        (await new DistributedRateLimitService(factory.Context, new FixedTimeProvider(MidWindowInstant))
            .TryAcquireAsync("auth", "user:deadline")).CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task Coordination_Npgsql_EmitsPartitionLockWithCallerToken()
    {
        var commands = new LockCommandCapture();
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused;Username=unused")
            .AddInterceptors(new SuppressedConnection(), commands).Options;
        using var context = new OrbitDbContext(options);
        using var cancellation = new CancellationTokenSource();
        var service = new DistributedRateLimitService(context, new FixedTimeProvider(MidWindowInstant));

        await service.AcquireTransactionLockAsync("auth", "user:ordered", cancellation.Token);

        commands.Sql.Should().ContainSingle().Which.Should()
            .Be("""
                LOCK TABLE "DistributedRateLimitBuckets" IN ROW EXCLUSIVE MODE;
                SELECT pg_advisory_xact_lock(hashtext(@p0), hashtext(@p1))
                """);
        commands.Tokens.Should().ContainSingle().Which.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task Coordination_Sqlite_DoesNotEmitPostgresLockSql()
    {
        var commands = new LockCommandCapture();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        commands.Sql.Clear();
        var service = new DistributedRateLimitService(factory.Context, new FixedTimeProvider(MidWindowInstant));

        await service.AcquireTransactionLockAsync("auth", "user:ordered", CancellationToken.None);

        commands.Sql.Should().BeEmpty();
    }

    private sealed class CoordinatedService(OrbitDbContext context, Func<CancellationToken, Task> coordinate,
        DatabaseConnectionSettings? settings = null)
        : DistributedRateLimitService(context, new FixedTimeProvider(MidWindowInstant), settings)
    {
        internal override Task AcquireTransactionLockAsync(string policyName, string partitionKey, CancellationToken cancellationToken) => coordinate(cancellationToken);
    }

    private sealed class AcquisitionCommands : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) Count++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SuppressedConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class LockCommandCapture : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            Tokens.Add(cancellationToken);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }
    }
}
