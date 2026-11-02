using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Orbit.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedReadMigrationRetryTests(ITestOutputHelper output)
{
    private const string StateBaseline = "20261102102120_PersistStripeCustomerErasureRecovery";
    private const string UsageIndex = "IX_AiUsageDaily_UserId";
    private const string AwardIndex = "IX_XpAwardLogs_UserId_Source_SourceId";
    private const string AttemptIndex = "IX_Users_GoogleCalendarAutoSyncEnabled_GoogleCalendarLastAttem~";

    [PostgresTheory]
    [InlineData("AiUsageDaily", UsageIndex, false)]
    [InlineData("AiUsageDaily", UsageIndex, true)]
    [InlineData("XpAwardLogs", AwardIndex, false)]
    [InlineData("XpAwardLogs", AwardIndex, true)]
    [InlineData("Users", AttemptIndex, false)]
    [InlineData("Users", AttemptIndex, true)]
    public Task ConcurrentBuildFailure_AllowsFreshMigrationRetry(string table, string index, bool leaveInvalid) =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            await using var blocker = new NpgsqlConnection(db.Database.GetConnectionString());
            await blocker.OpenAsync();
            var failure = new BlockIndexBuild(blocker, table, index, leaveInvalid);
            await using (var migrating = CreateContext(db, failure))
            {
                var exception = await Record.ExceptionAsync(() => migrating.Database.MigrateAsync());
                exception.Should().NotBeNull();
                (exception?.GetBaseException()).Should().BeOfType<TimeoutException>();
                failure.Attempts.Should().Be(1);
                output.WriteLine("Initial failure: " + exception);
            }
            await blocker.CloseAsync();

            var valid = await IndexValidity(db, index);
            if (leaveInvalid) valid.Should().BeFalse("the failed PostgreSQL build leaves an unusable index");
            else valid.Should().BeNull("the build was blocked before creating its catalog entry");
            await RetryAndVerify(db, index);
        }, Baseline(table));

    [PostgresTheory]
    [InlineData("AiUsageDaily", UsageIndex)]
    [InlineData("XpAwardLogs", AwardIndex)]
    [InlineData("Users", AttemptIndex)]
    public Task InterruptionAfterSuccessfulBuild_AllowsFreshMigrationRetry(string table, string index) =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var interruption = new InterruptAfterIndex(index);
            await using (var migrating = CreateContext(db, interruption))
            {
                var exception = await Record.ExceptionAsync(() => migrating.Database.MigrateAsync());
                exception.Should().BeOfType<OperationCanceledException>();
                interruption.Interrupted.Should().BeTrue();
            }
            (await IndexValidity(db, index)).Should().BeTrue();
            await RetryAndVerify(db, index);
        }, Baseline(table));

    private static string Baseline(string table) => table == "AiUsageDaily"
        ? "20261102071352_DropGeneralTaskCompletionBackfill" : StateBaseline;

    [PostgresTheory]
    [InlineData("AiUsageDaily", UsageIndex)]
    [InlineData("XpAwardLogs", AwardIndex)]
    [InlineData("Users", AttemptIndex)]
    public Task AutomaticRetry_DoesNotAcceptInvalidIndex(string table, string index) =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            await using var blocker = new NpgsqlConnection(db.Database.GetConnectionString());
            await blocker.OpenAsync();
            var injection = new BlockIndexBuild(blocker, table, index, leaveInvalid: true);
            await using (var migrating = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
                .UseNpgsql(db.Database.GetConnectionString(), options => options.EnableRetryOnFailure(
                    maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
                .AddInterceptors(injection).Options))
            {
                var failure = await Record.ExceptionAsync(() => migrating.Database.MigrateAsync());
                (await IndexValidity(db, index)).Should().BeFalse();
                failure.Should().NotBeNull("an automatic retry must not report success over an invalid index");
                injection.Attempts.Should().BeGreaterThan(1);
            }
            await blocker.CloseAsync();
            (await IndexValidity(db, index)).Should().BeFalse();
            (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(table == "AiUsageDaily"
                ? "20261102072150_RestoreBoundedReadIndexes" : "20261102193912_CompleteBoundedReadIndexes");
            await RetryAndVerify(db, index);
        }, Baseline(table));

    private async Task RetryAndVerify(OrbitDbContext db, string index)
    {
        await using var retry = CreateContext(db);
        var migration = () => retry.Database.MigrateAsync();
        await migration.Should().NotThrowAsync("a fresh release invocation must recover without schema repair");
        foreach (var name in new[] { UsageIndex, AwardIndex, AttemptIndex })
            (await IndexValidity(db, name)).Should().BeTrue();
        (await retry.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        retry.Database.HasPendingModelChanges().Should().BeFalse();
        output.WriteLine("Fresh-context retry recovered " + index);
    }

    private static Task<bool?> IndexValidity(OrbitDbContext db, string name) => db.Database.SqlQuery<bool?>($"""
        SELECT i.indisvalid AS "Value" FROM pg_index i
        JOIN pg_class c ON c.oid = i.indexrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relname = {name}
        """).SingleOrDefaultAsync();

    private static OrbitDbContext CreateContext(OrbitDbContext db, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(db.Database.GetConnectionString(), options => options.CommandTimeout(15))
            .AddInterceptors(interceptors).Options);

    private sealed class BlockIndexBuild(NpgsqlConnection blocker, string table, string index, bool leaveInvalid) : DbCommandInterceptor
    {
        public int Attempts { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsBuild(command.CommandText, index) && Attempts++ == 0)
            {
                var mode = leaveInvalid ? "ROW EXCLUSIVE" : "SHARE UPDATE EXCLUSIVE";
                await using var hold = new NpgsqlCommand($"BEGIN; LOCK TABLE \"{table}\" IN {mode} MODE;", blocker);
                await hold.ExecuteNonQueryAsync(cancellationToken);
                command.CommandTimeout = 1;
            }
            return result;
        }
    }

    private sealed class InterruptAfterIndex(string index) : DbCommandInterceptor
    {
        private bool built;
        public bool Interrupted { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (built)
            {
                Interrupted = true;
                throw new OperationCanceledException("Migration interrupted after its concurrent index committed.");
            }
            built = IsBuild(command.CommandText, index);
            return ValueTask.FromResult(result);
        }
    }

    private static bool IsBuild(string sql, string index) =>
        sql.Contains("CREATE INDEX CONCURRENTLY", StringComparison.Ordinal)
        && sql.Contains('"' + index + '"', StringComparison.Ordinal);
}
