using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Gamification.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class XpHistoryStoreTests
{
    [PostgresFact]
    public Task HistoricalRange_UsesStoredBaselineAndKeepsLateAndLegacyAwardsConsistent() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var user = User.Create("XP", "xp-history-store@example.test").Value;
            user.SetStripeSubscription("sub", DateTime.UtcNow.AddYears(1));
            var today = new DateOnly(2026, 6, 15);
            var first = today.AddDays(-730);
            var awards = new List<XpAwardLog>();
            for (var date = first; date <= today.AddDays(5); date = date.AddDays(1))
                awards.Add(XpAwardLog.Create(user.Id, 10, XpAwardSource.HabitLog, null, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
            var total = awards.Sum(x => x.Amount);
            user.AddXp(total);
            db.Users.Add(user);
            db.XpAwardLogs.AddRange(awards);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            user = await db.Users.FindAsync(user.Id) ?? throw new InvalidOperationException();
            await new XpAwarder(new GenericRepository<XpAwardLog>(db)).AwardAsync(user, 7, XpAwardSource.HabitLog,
                null, today.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            await db.SaveChangesAsync();
            var counter = new CountingDbCommandInterceptor();
            await using var readDb = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
                .UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(counter).Options);
            var flags = Substitute.For<IFeatureFlagService>();
            flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
            var store = new XpHistoryStore(readDb);
            var result = await new GetXpHistoryQueryHandler(new GenericRepository<User>(readDb), new GenericRepository<XpAwardLog>(readDb), flags, store)
                .Handle(new(user.Id, today.AddDays(-3), today.AddDays(-1)), default);
            result.IsSuccess.Should().BeTrue();
            result.Value.Points.Select(p => p.DailyXp).Should().Equal(10, 17, 10);
            result.Value.Points.Last().CumulativeXp.Should().Be(total - 60 + 7);
            var awardReads = counter.Commands.Where(c => c.Sql.Contains("\"XpAwardLogs\"", StringComparison.Ordinal)).ToArray();
            awardReads.Should().ContainSingle();
            awardReads[0].Rows.Should().Be(4);
            awardReads[0].Sql.Should().Contain(">=").And.Contain("<");
            counter.Commands.Count(c => c.Sql.Contains("\"XpDailyTotals\"", StringComparison.Ordinal)).Should().Be(1);
            var baseline = await store.ReadBaselineAsync(user.Id, today);
            var legacyId = Guid.NewGuid();
            var awardedAtUtc = today.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "XpAwardLogs" ("Id", "UserId", "Amount", "Source", "SourceId", "AwardedAtUtc", "CreatedAtUtc")
                VALUES ({legacyId}, {user.Id}, 5, 0, NULL, {awardedAtUtc}, {awardedAtUtc})
                """);
            (await store.ReadBaselineAsync(user.Id, today)).Should().Be(baseline + 5);
            await db.XpAwardLogs.Where(row => row.Id == legacyId).ExecuteDeleteAsync();
            (await store.ReadBaselineAsync(user.Id, today)).Should().Be(baseline);
        });
}
