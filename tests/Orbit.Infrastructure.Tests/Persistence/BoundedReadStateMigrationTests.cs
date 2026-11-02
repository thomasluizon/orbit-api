using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedReadStateMigrationTests
{
    private const string Before = "20261102072150_RestoreBoundedReadIndexes";

    [PostgresFact]
    public Task Migration_BackfillsMoreThanOneUserBatchAndRebuildsAfterRollback() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var firstAtUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var secondAtUtc = firstAtUtc.AddDays(1);
            for (var index = 0; index < 101; index++)
            {
                var userId = Guid.NewGuid();
                var email = $"backfill-{index}@example.test";
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Users" ("Id", "Name", "Email", "CreatedAtUtc") VALUES ({userId}, 'Backfill', {email}, {firstAtUtc})
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "XpAwardLogs" ("Id", "UserId", "Amount", "Source", "SourceId", "AwardedAtUtc", "CreatedAtUtc")
                    VALUES ({Guid.NewGuid()}, {userId}, 10, 0, NULL, {firstAtUtc}, {firstAtUtc}),
                        ({Guid.NewGuid()}, {userId}, 2, 0, NULL, {secondAtUtc}, {secondAtUtc})
                    """);
            }
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync();
            (await db.XpDailyTotals.CountAsync()).Should().Be(202);
            (await db.XpDailyTotals.Where(x => x.Date == new DateOnly(2026, 6, 2)).Select(x => x.CumulativeXp).ToArrayAsync())
                .Should().HaveCount(101).And.OnlyContain(value => value == 12);
            db.Database.HasPendingModelChanges().Should().BeFalse();
            await migrator.MigrateAsync(Before);
            (await db.Database.SqlQueryRaw<bool>("SELECT to_regprocedure('maintain_xp_daily_totals()') IS NULL AS \"Value\"").SingleAsync()).Should().BeTrue();
            await migrator.MigrateAsync();
            (await db.XpDailyTotals.CountAsync()).Should().Be(202);
        }, Before);
}
