using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OAuthStateStoreRetryTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void Put_TransientFailure_ReplaysTransactionWithoutRetainingFailedInsert(int? capacity, bool failAtCommit)
    {
        var failure = new FailureProbe(failAtCommit);
        using var database = new SqliteOrbitDbContextFactory(
            dependencies => new NpgsqlRetryingExecutionStrategy(dependencies, 1, TimeSpan.Zero, null),
            failure, new SaveProbe(failure));
        var db = database.Context;
        var store = new OAuthStateStore(db, TimeProvider.System);
        var expiresAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(5);
        failure.Enabled = true;

        store.Put("secret", "google", "payload", expiresAtUtc, capacity).Should().BeTrue();

        failure.Starts.Should().Be(2);
        failure.Saves.Should().Be(2);
        failure.Commits.Should().Be(failAtCommit ? 2 : 1);
        failure.Failures.Should().Be(1);
        db.ChangeTracker.Entries<OAuthState>().Should().BeEmpty();
        db.OAuthStates.AsNoTracking().Should().ContainSingle().Which.Payload.Should().Be("payload");
        store.Read("secret", "google", TimeProvider.System.GetUtcNow().UtcDateTime).Should().Be("payload");
        if (capacity.HasValue)
        {
            store.Put("overflow", "google", "overflow", expiresAtUtc, capacity).Should().BeFalse();
            store.Put("other-kind", "consent", "consent", expiresAtUtc, capacity).Should().BeTrue();
        }

        db.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ambient = db.Database.BeginTransaction();
            var starts = failure.Starts;
            store.Put("ambient", "code", "code", expiresAtUtc).Should().BeTrue();
            db.Database.CurrentTransaction.Should().BeSameAs(ambient);
            failure.Starts.Should().Be(starts);
        });
        store.Read("ambient", "code", TimeProvider.System.GetUtcNow().UtcDateTime).Should().BeNull();
    }

    private sealed class FailureProbe(bool failAtCommit) : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public int Starts { get; private set; }
        public int Saves { get; set; }
        public int Commits { get; private set; }
        public int Failures { get; private set; }

        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            if (Enabled)
            {
                ExecutionStrategy.Current.Should().BeOfType<NpgsqlRetryingExecutionStrategy>();
                Starts++;
            }
            return result;
        }

        public override InterceptionResult TransactionCommitting(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result)
        {
            if (Enabled)
            {
                Commits++;
                if (failAtCommit) FailOnce();
            }
            return result;
        }

        public void BeforeSave(DbContext context)
        {
            if (!Enabled) return;
            Saves++;
            context.ChangeTracker.Entries<OAuthState>().Should().ContainSingle();
            if (!failAtCommit) FailOnce();
        }

        private void FailOnce()
        {
            if (Failures > 0) return;
            Failures++;
            throw new NpgsqlException("Transient OAuth state write failure", new TimeoutException());
        }
    }

    private sealed class SaveProbe(FailureProbe failure) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            failure.BeforeSave(eventData.Context ?? throw new InvalidOperationException("Missing save context"));
            return result;
        }
    }
}
