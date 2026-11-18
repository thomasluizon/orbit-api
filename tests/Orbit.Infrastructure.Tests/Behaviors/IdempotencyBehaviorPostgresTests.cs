using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Behaviors;

public class IdempotencyBehaviorPostgresTests
{
    private const string Key = "mutation-key-1";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RetryWithTheSameKeyWhileTheFirstCallRuns_RunsTheHandlerOnce(bool callsProvider) =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var userId = await SeedUserAsync(db);
            await using var firstRequest = new KeyedRequest(db, userId);
            await using var retryRequest = new KeyedRequest(db, userId);
            await using var replayRequest = new KeyedRequest(db, userId);
            var handlerCalls = 0;
            var firstRuns = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var openTransaction = false;
            RequestHandlerDelegate<string> counted = _ =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult("second");
            };

            var first = Task.Run(() => firstRequest.Behavior.Handle(new(callsProvider), async _ =>
            {
                Interlocked.Increment(ref handlerCalls);
                openTransaction = firstRequest.Work.HasActiveTransaction;
                firstRuns.SetResult();
                await finishFirst.Task.WaitAsync(Patience);
                return "first";
            }, default));
            await firstRuns.Task.WaitAsync(Patience);
            var reservationsOfOtherRequests = await db.ProcessedRequests.AsNoTracking().CountAsync();
            var retry = Task.Run(() => retryRequest.Behavior.Handle(new(callsProvider), counted, default));
            await Task.WhenAny(retry, Task.Delay(callsProvider ? Patience : TimeSpan.FromSeconds(1)));
            var retrySettledWhileTheFirstCallRan = retry.IsCompleted;
            finishFirst.SetResult();
            var firstFailure = await Record.ExceptionAsync(() => first);
            var retryFailure = await Record.ExceptionAsync(() => retry);

            handlerCalls.Should().Be(1, "a retry with the same key must not run the call again while the first call runs");
            firstFailure.Should().BeNull();
            (await first).Should().Be("first");
            openTransaction.Should().Be(!callsProvider);
            if (callsProvider)
            {
                reservationsOfOtherRequests.Should().Be(1, "the key is reserved and committed before the provider call");
                retrySettledWhileTheFirstCallRan.Should().BeTrue("the retry does not wait for the provider call");
                retryFailure.Should().BeOfType<DbUpdateException>();
                DbUniqueViolation.IsUniqueViolation(retryFailure!).Should().BeTrue();
            }
            else
            {
                reservationsOfOtherRequests.Should().Be(0);
                retrySettledWhileTheFirstCallRan.Should().BeFalse("the retry waits for the transaction of the first call");
                retryFailure.Should().BeNull();
                (await retry).Should().Be("first");
            }
            (await replayRequest.Behavior.Handle(new(callsProvider), counted, default)).Should().Be("first");
            handlerCalls.Should().Be(1);
            (await db.ProcessedRequests.AsNoTracking().CountAsync()).Should().Be(1);
        });

    [PostgresFact]
    public Task ProviderCallOfAStoppedProcess_LeavesAReservationThatGivesTheConflictAnswerAndRunsNoHandler() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var userId = await SeedUserAsync(db);
            await using var retryRequest = new KeyedRequest(db, userId);
            var handlerCalls = 0;
            var firstRuns = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var neverFinishes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var stoppedRequest = new KeyedRequest(db, userId))
            {
                _ = Task.Run(() => stoppedRequest.Behavior.Handle(new(true), async _ =>
                {
                    Interlocked.Increment(ref handlerCalls);
                    firstRuns.SetResult();
                    await neverFinishes.Task;
                    return "first";
                }, default));
                await firstRuns.Task.WaitAsync(Patience);
            }

            var retry = () => retryRequest.Behavior.Handle(new(true), _ =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult("second");
            }, default);

            DbUniqueViolation.IsUniqueViolation((await retry.Should().ThrowAsync<DbUpdateException>()).Which).Should().BeTrue();
            handlerCalls.Should().Be(1, "the stopped call can hold committed writes, so the key never runs a second call");
            (await db.ProcessedRequests.AsNoTracking().Select(saved => saved.ResponseBody).ToListAsync()).Should().Equal("");
        });

    [PostgresFact]
    public Task ProviderCallThatFails_ReleasesTheReservationAndALaterRetryRuns() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var userId = await SeedUserAsync(db);
            await using var failedRequest = new KeyedRequest(db, userId);
            await using var retryRequest = new KeyedRequest(db, userId);
            await using var replayRequest = new KeyedRequest(db, userId);
            var reservationsDuringTheCall = -1;

            var failed = () => failedRequest.Behavior.Handle(new(true), async _ =>
            {
                reservationsDuringTheCall = await db.ProcessedRequests.AsNoTracking().CountAsync();
                failedRequest.Context.Tags.Add(Tag.Create(userId, "pending-tag", "#ff0000").Value);
                throw new InvalidOperationException("The provider failed");
            }, default);

            await failed.Should().ThrowAsync<InvalidOperationException>();
            reservationsDuringTheCall.Should().Be(1, "the key is reserved and committed before the provider call");
            (await db.ProcessedRequests.AsNoTracking().CountAsync()).Should().Be(0);
            (await db.Tags.AsNoTracking().CountAsync()).Should().Be(0);
            (await retryRequest.Behavior.Handle(new(true), _ => Task.FromResult("answer"), default)).Should().Be("answer");
            (await replayRequest.Behavior.Handle(new(true), _ => Task.FromResult("second"), default)).Should().Be("answer");
            (await db.ProcessedRequests.AsNoTracking().CountAsync()).Should().Be(1);
        });

    private static async Task<Guid> SeedUserAsync(OrbitDbContext db)
    {
        var user = User.Create("Idempotency", $"idempotency-{Guid.NewGuid():N}@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private sealed record ProviderRequest(bool CallsProvider) : IRequest<string>, IIdempotentCommand, IProviderCallCommand;

    private sealed class KeyedRequest : IAsyncDisposable
    {
        public KeyedRequest(OrbitDbContext database, Guid userId)
        {
            Context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
                .UseNpgsql(database.Database.GetConnectionString(),
                    postgres => postgres.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
                .Options);
            Work = new UnitOfWork(Context, new DatabaseConnectionSettings());
            Behavior = new IdempotencyBehavior<ProviderRequest, string>(
                new KeyContext(userId), new IdempotencyStore(Context), Work);
        }

        public OrbitDbContext Context { get; }
        public UnitOfWork Work { get; }
        public IdempotencyBehavior<ProviderRequest, string> Behavior { get; }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class KeyContext(Guid userId) : IIdempotencyContext
    {
        public bool TryGetRequestKey(out Guid resolvedUserId, out string idempotencyKey)
        {
            resolvedUserId = userId;
            idempotencyKey = Key;
            return true;
        }

        public int NextRequestOrdinal(string requestType) => 0;
    }
}
