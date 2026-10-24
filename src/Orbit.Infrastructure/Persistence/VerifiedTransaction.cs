using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Orbit.Infrastructure.Persistence;

internal static class VerifiedTransaction
{
    public static T Execute<T>(OrbitDbContext db, Func<T> operation)
    {
        if (db.Database.CurrentTransaction is not null)
            return operation();

        var id = Guid.NewGuid();
        var candidate = new ExecutionResult<T>(false, default!);
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.Execute(operation, (_, execute) =>
        {
            candidate = new(false, default!);
            using var transaction = db.Database.BeginTransaction();
            var result = execute();
            if (strategy.RetriesOnFailure)
            {
                var nowAtUtc = DateTime.UtcNow;
                var cutoffAtUtc = nowAtUtc.AddDays(-1);
                if (db.Database.IsNpgsql())
                    db.Database.ExecuteSqlInterpolated(ExpiredReceipts(cutoffAtUtc));
                else
                    db.Set<TransactionCommit>().Where(commit => commit.CreatedAtUtc < cutoffAtUtc).Take(100).ExecuteDelete();
                db.Database.ExecuteSqlInterpolated($"INSERT INTO \"TransactionCommits\" (\"Id\", \"CreatedAtUtc\") VALUES ({id}, {nowAtUtc})");
            }
            candidate = new(true, result);
            transaction.Commit();
            return result;
        }, (_, _) => new ExecutionResult<T>(candidate.IsSuccessful
            && db.Set<TransactionCommit>().AsNoTracking().Any(commit => commit.Id == id), candidate.Result));
    }

    public static async Task<T> ExecuteAsync<T>(OrbitDbContext db, Func<CancellationToken, Task<T>> operation,
        Action discardAttempt, CancellationToken cancellationToken, bool verifyCommit = true)
    {
        var id = Guid.NewGuid();
        var candidate = new ExecutionResult<T>(false, default!);
        var retry = false;
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(operation, async (_, execute, token) =>
            {
                if (retry) discardAttempt();
                retry = true;
                candidate = new(false, default!);
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var result = await execute(token);
                if (verifyCommit && strategy.RetriesOnFailure)
                {
                    var nowAtUtc = DateTime.UtcNow;
                    var cutoffAtUtc = nowAtUtc.AddDays(-1);
                    if (db.Database.IsNpgsql())
                        await db.Database.ExecuteSqlInterpolatedAsync(ExpiredReceipts(cutoffAtUtc), token);
                    else
                        await db.Set<TransactionCommit>().Where(commit => commit.CreatedAtUtc < cutoffAtUtc).Take(100).ExecuteDeleteAsync(token);
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO \"TransactionCommits\" (\"Id\", \"CreatedAtUtc\") VALUES ({id}, {nowAtUtc})", token);
                }
                candidate = new(true, result);
                await transaction.CommitAsync(token);
                return result;
            }, async (_, _, token) => new ExecutionResult<T>(verifyCommit && candidate.IsSuccessful
                && await db.Set<TransactionCommit>().AsNoTracking().AnyAsync(commit => commit.Id == id, token), candidate.Result),
                cancellationToken);
        }
        catch
        {
            discardAttempt();
            throw;
        }
    }

    private static FormattableString ExpiredReceipts(DateTime cutoffAtUtc) => $"""
        DELETE FROM "TransactionCommits" WHERE "Id" IN (
            SELECT "Id" FROM "TransactionCommits"
            WHERE "CreatedAtUtc" < {cutoffAtUtc}
            ORDER BY "CreatedAtUtc"
            LIMIT 100 FOR UPDATE SKIP LOCKED
        )
        """;
}
