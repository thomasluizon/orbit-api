using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ReferralRewardMigrationTests
{
    [Fact]
    public void Migration_IsAdditiveWithSafeDefaultsAndIdempotentHistoryGuard()
    {
        var migration = new AddReferralProDayRewards { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        RollingMigrationGate.Check("expand", migration.UpOperations).Should().BeEmpty();
        migration.UpOperations.OfType<AddColumnOperation>().Should().OnlyContain(c => c.IsNullable || c.DefaultValue != null);
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var script = context.GetService<IMigrator>().GenerateScript("20261019165331_ExpandHabitScheduleAndStartDate",
            "20261020023828_AddReferralProDayRewards", MigrationsSqlGenerationOptions.Idempotent);
        script.Should().Contain("IF NOT EXISTS(SELECT 1 FROM \"__EFMigrationsHistory\"")
            .And.Contain("ADD \"BonusProExpiresAtUtc\" timestamp with time zone");
    }

    [Fact]
    public async Task Backfill_PreservesHistoricalRewardAndEntitlementsAndIsRepeatable()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var inviter = User.Create("Inviter", "inviter@example.com").Value;
        var invitee = User.Create("Invitee", "invitee@example.com").Value;
        var historical = Referral.Create(inviter.Id, invitee.Id);
        historical.MarkRewarded();
        var pending = Referral.Create(inviter.Id, Guid.NewGuid());
        db.AddRange(inviter, invitee, historical, pending);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Referrals\" SET \"ReferrerReward\" = 0, \"ReferredReward\" = 0");
        db.ChangeTracker.Clear();
        var sql = new AddReferralProDayRewards().UpOperations.OfType<SqlOperation>().Single().Sql;

        (await db.Database.ExecuteSqlRawAsync(sql)).Should().Be(1);
        (await db.Database.ExecuteSqlRawAsync(sql)).Should().Be(0);

        var persisted = await db.Referrals.SingleAsync(r => r.Id == historical.Id);
        persisted.ReferrerReward.Should().Be(ReferralRewardKind.Coupon);
        persisted.ReferredReward.Should().Be(ReferralRewardKind.Coupon);
        persisted.RewardGrantedAtUtc.Should().Be(historical.RewardGrantedAtUtc);
        persisted.ReferrerRewardDeliveredAtUtc.Should().Be(historical.RewardGrantedAtUtc);
        (await db.Users.ToListAsync()).Should().AllSatisfy(u => u.PlanExpiresAt.Should().BeNull());
        (await db.Referrals.SingleAsync(r => r.Id == pending.Id)).RewardGrantedAtUtc.Should().BeNull();
    }
}
