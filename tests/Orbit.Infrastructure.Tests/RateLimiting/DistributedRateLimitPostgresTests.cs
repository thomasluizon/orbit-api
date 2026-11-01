using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public class DistributedRateLimitPostgresTests(ITestOutputHelper output)
{
    [PostgresFact]
    public Task RequestAbortedBeforeWrite_RollsBackWithoutTransactionError() => CancellationAt("read", false);

    [PostgresFact]
    public Task RequestAbortedDuringWrite_CompletesWithoutTransactionError() => CancellationAt("write", true);

    [PostgresFact]
    public Task RequestAbortedBeforeCommit_CompletesWithoutTransactionError() => CancellationAt("saved", true);

    [PostgresFact]
    public Task RequestAbortedAfterCommitStarts_CompletesWithoutTransactionError() => CancellationAt("commit", true);

    private Task CancellationAt(string phase, bool completes) => PostgresTestDatabase.WithDatabase(async database =>
    {
        using var caller = new CancellationTokenSource();
        var commands = new AcquisitionCommands
        {
            Before = command =>
        {
            if ((phase == "write" && command.CommandText.StartsWith("INSERT INTO \"DistributedRateLimitBuckets\"", StringComparison.Ordinal))
                || (phase == "read" && IsBucketRead(command)))
                caller.Cancel();
            return Task.CompletedTask;
        }
        };
        var transactions = new TransactionProbe { Committing = () => { if (phase == "commit") caller.Cancel(); } };
        var save = new AfterSave(() => { if (phase == "saved") caller.Cancel(); });
        await using var db = Context(database, commands, transactions, save);
        var acquire = () => ExecuteFilter(db, "refresh", "192.0.2.1", caller.Token);
        var failure = await Record.ExceptionAsync(async () => { await acquire(); });
        output.WriteLine($"Phase={phase}; exception={failure}; transactions={string.Join("; ", transactions.Failures)}");

        caller.IsCancellationRequested.Should().BeTrue();
        transactions.Failures.Should().BeEmpty();
        if (completes)
            failure.Should().BeNull();
        else
            failure.Should().BeAssignableTo<OperationCanceledException>();
        db.Database.CurrentTransaction.Should().BeNull();
        db.ChangeTracker.HasChanges().Should().BeFalse();
        (await database.DistributedRateLimitBuckets.SumAsync(bucket => bucket.Count)).Should().Be(completes ? 1 : 0);
        (await ExecuteFilter(db, "refresh", "192.0.2.2")).Should().BeTrue();
    });

    [PostgresFact]
    public Task SlowAcquisition_DoesNotBlockOtherPartitionsOrPolicies() => PostgresTestDatabase.WithDatabase(async database =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new AcquisitionCommands
        {
            Before = async command =>
        {
            if (IsBucketRead(command))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        };
        await using var heldContext = Context(database, commands);
        var held = ExecuteFilter(heldContext, "refresh", "192.0.2.1");
        var others = new List<Task<bool>>();
        var completedWhileHeld = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var (policy, ip) in new[] { ("refresh", "192.0.2.2"), ("auth", "192.0.2.1"), ("chat", "192.0.2.3") })
                others.Add(AcquireSeparately(database, policy, ip, new AcquisitionCommands { Delay = TimeSpan.FromMilliseconds(100) }));
            var all = Task.WhenAll(others);
            completedWhileHeld = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3))) == all;
        }
        finally
        {
            release.TrySetResult();
            (await held).Should().BeTrue();
            (await Task.WhenAll(others)).Should().OnlyContain(allowed => allowed);
        }
        completedWhileHeld.Should().BeTrue("unrelated acquisitions must finish while the first transaction still owns its lock");
    });

    [PostgresFact]
    public Task ConcurrentFixedWindow_AdmitsExactlyPermitLimit() => ExactLimit("auth", 10);

    [PostgresFact]
    public Task ConcurrentSegmentedWindow_AdmitsExactlyPermitLimit() => ExactLimit("chat", 20);

    [PostgresFact]
    public Task RejectedAcquisition_DoesNotCommit() => PostgresTestDatabase.WithDatabase(async database =>
    {
        await using var db = Context(database);
        for (var i = 0; i < 3; i++)
            (await ExecuteFilter(db, "support", "192.0.2.1")).Should().BeTrue();
        var transactions = new TransactionProbe();
        await using var rejected = Context(database, transactions);

        (await ExecuteFilter(rejected, "support", "192.0.2.1")).Should().BeFalse();

        transactions.CommitAttempts.Should().Be(0);
        transactions.Failures.Should().BeEmpty();
        rejected.Database.CurrentTransaction.Should().BeNull();
        (await database.DistributedRateLimitBuckets.SumAsync(bucket => bucket.Count)).Should().Be(3);
    });

    [PostgresFact]
    public Task LockWait_HonorsServerDeadlineAndRestoresCompletionToken() => PostgresTestDatabase.WithDatabase(async database =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new AcquisitionCommands
        {
            Before = async command =>
        {
            if (IsBucketRead(command))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        };
        await using var heldContext = Context(database, commands);
        var held = ExecuteFilter(heldContext, "refresh", "192.0.2.1");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var transactions = new TransactionProbe();
            await using var waiting = Context(database, transactions);
            using var previous = new CancellationTokenSource();
            waiting.TransactionCompletionToken = previous.Token;
            var acquire = () => ExecuteFilter(waiting, "refresh", "192.0.2.1",
                settings: new DatabaseConnectionSettings { TransactionTimeoutSeconds = 1 });

            await acquire.Should().ThrowAsync<TimeoutException>();

            waiting.TransactionCompletionToken.Should().Be(previous.Token);
            waiting.Database.CurrentTransaction.Should().BeNull();
            transactions.Failures.Should().BeEmpty();
            (await database.DistributedRateLimitBuckets.CountAsync()).Should().Be(0);
        }
        finally
        {
            release.TrySetResult();
            (await held).Should().BeTrue();
        }
    });

    private static Task ExactLimit(string policy, int limit) => PostgresTestDatabase.WithDatabase(async database =>
    {
        var previousSegmentCount = policy == "chat" ? 7 : 0;
        if (previousSegmentCount > 0)
        {
            var previousSegment = new FixedClock().GetUtcNow().UtcDateTime.AddSeconds(-15);
            database.DistributedRateLimitBuckets.Add(DistributedRateLimitBucket.Create(
                policy, "ip:192.0.2.1", previousSegment, previousSegment.AddMinutes(1), previousSegmentCount));
            await database.SaveChangesAsync();
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 40).Select(async _ =>
        {
            await start.Task;
            return await AcquireSeparately(database, policy, "192.0.2.1", new AcquisitionCommands { Delay = TimeSpan.FromMilliseconds(10) });
        }).ToArray();
        start.SetResult();
        var decisions = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(45));
        decisions.Count(allowed => allowed).Should().Be(limit - previousSegmentCount);
        (await database.DistributedRateLimitBuckets.SumAsync(bucket => bucket.Count)).Should().Be(limit);
    });

    private static async Task<bool> AcquireSeparately(OrbitDbContext database, string policy, string ip, params IInterceptor[] interceptors)
    {
        await using var db = Context(database, interceptors);
        return await ExecuteFilter(db, policy, ip);
    }

    private static OrbitDbContext Context(OrbitDbContext database, params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<OrbitDbContext>().UseNpgsql(database.Database.GetConnectionString())
            .AddInterceptors(interceptors).Options);

    private static async Task<bool> ExecuteFilter(OrbitDbContext db, string policy, string ip, CancellationToken token = default,
        DatabaseConnectionSettings? settings = null)
    {
        var http = new DefaultHttpContext { RequestAborted = token };
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        http.Request.Method = "POST";
        http.Request.Path = "/oauth/token";
        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [], new Dictionary<string, object?>(), new object());
        var filter = new DistributedRateLimitFilter(policy, new DistributedRateLimitService(db, new FixedClock(), settings),
            Substitute.For<IAuthSessionService>(), new BffClientIpResolver(Options.Create(new BffForwardingSettings())),
            NullLogger<DistributedRateLimitFilter>.Instance);
        var allowed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            allowed = true;
            return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
        });
        if (!allowed)
        {
            context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
            http.Response.Headers.RetryAfter.Should().NotBeNullOrEmpty();
        }
        return allowed;
    }

    private static bool IsBucketRead(DbCommand command) => command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
        && command.CommandText.Contains("FROM \"DistributedRateLimitBuckets\"", StringComparison.Ordinal);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 15, 12, 30, 30, TimeSpan.Zero);
    }

    private sealed class AcquisitionCommands : DbCommandInterceptor
    {
        public Func<DbCommand, Task> Before { get; init; } = _ => Task.CompletedTask;
        public TimeSpan Delay { get; init; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await Before(command);
            await Task.Delay(Delay, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await Before(command);
            await Task.Delay(Delay, cancellationToken);
            return result;
        }
    }

    private sealed class AfterSave(Action saved) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            saved();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TransactionProbe : DbTransactionInterceptor
    {
        public int CommitAttempts { get; private set; }
        public ConcurrentQueue<string> Failures { get; } = new();
        public Action Committing { get; init; } = () => { };

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            CommitAttempts++;
            Committing();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Failures.Enqueue($"{eventData.Action}: {eventData.Exception.GetType().FullName}: {eventData.Exception.Message}");
            return Task.CompletedTask;
        }
    }
}
