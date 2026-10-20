using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class SeparateBonusProEntitlementMigrationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(SubscriptionSource.Stripe, false)]
    [InlineData(SubscriptionSource.GooglePlay, false)]
    [InlineData(null, true)]
    [InlineData(SubscriptionSource.Stripe, true)]
    [InlineData(SubscriptionSource.GooglePlay, true)]
    public async Task UpAndDown_NormalizeLegacyBonusWithoutLosingAccess(
        SubscriptionSource? source, bool expiredBonus)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var clock = new Clock(DateTimeOffset.UtcNow.AddDays(expiredBonus ? -8 : 0));
        var user = User.Create("Bonus", "bonus@example.com", clock).Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        if (source == SubscriptionSource.Stripe)
            user.SetStripeSubscription("sub_expired", clock.GetUtcNow().UtcDateTime.AddDays(-1));
        else if (source == SubscriptionSource.GooglePlay)
            user.SetPlaySubscription("play-token", clock.GetUtcNow().UtcDateTime.AddDays(-1));
        user.GrantBonusProDays(7);
        var bonusExpiry = user.BonusProExpiresAtUtc;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Users" SET "Plan" = 1, "PlanExpiresAt" = "BonusProExpiresAtUtc" WHERE "Id" = {user.Id}
            """);
        db.ChangeTracker.Clear();
        var migration = new SeparateBonusProEntitlement();
        var up = migration.UpOperations.OfType<SqlOperation>().Single().Sql;
        var down = migration.DownOperations.OfType<SqlOperation>().Single().Sql;
        RollingMigrationGate.Check("expand", migration.UpOperations).Should().BeEmpty();

        (await db.Database.ExecuteSqlRawAsync(up)).Should().Be(1);
        (await db.Database.ExecuteSqlRawAsync(up)).Should().Be(0);

        var normalized = await db.Users.SingleAsync();
        normalized.SubscriptionPlan.Should().Be(UserPlan.Free);
        normalized.SubscriptionExpiresAtUtc.Should().BeNull();
        normalized.BonusProExpiresAtUtc.Should().Be(bonusExpiry);
        normalized.PlanExpiresAt.Should().Be(bonusExpiry);
        normalized.HasProAccess.Should().Be(!expiredBonus);
        normalized.HasActivePaidSubscription.Should().BeFalse();
        normalized.SubscriptionSource.Should().Be(source);
        normalized.StripeSubscriptionId.Should().Be(source == SubscriptionSource.Stripe ? "sub_expired" : null);
        normalized.PlayPurchaseToken.Should().Be(source == SubscriptionSource.GooglePlay ? "play-token" : null);

        (await db.Database.ExecuteSqlRawAsync(down)).Should().Be(1);
        (await db.Database.ExecuteSqlRawAsync(down)).Should().Be(0);
        db.ChangeTracker.Clear();
        var restored = await db.Users.SingleAsync();
        restored.SubscriptionPlan.Should().Be(UserPlan.Pro);
        restored.SubscriptionExpiresAtUtc.Should().Be(bonusExpiry);
        restored.BonusProExpiresAtUtc.Should().Be(bonusExpiry);
        restored.HasProAccess.Should().Be(!expiredBonus);
        restored.SubscriptionSource.Should().Be(source);
    }

    [Fact]
    public async Task UpAndDown_PreserveProviderPeriodsAndCanceledBonus()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var now = DateTime.UtcNow;
        var paid = User.Create("Paid", "paid@example.com").Value;
        paid.SetStripeSubscription("sub_paid", now.AddDays(30));
        var expired = User.Create("Expired", "expired@example.com").Value;
        expired.StartTrial(now.AddDays(-30));
        expired.SetPlaySubscription("play-expired", now.AddDays(-1));
        var renewed = User.Create("Renewed", "renewed@example.com").Value;
        renewed.GrantBonusProDays(7);
        renewed.SetStripeSubscription("sub_renewed", now.AddDays(30));
        var canceled = User.Create("Canceled", "canceled@example.com").Value;
        canceled.StartTrial(now.AddDays(-30));
        canceled.GrantBonusProDays(7);
        db.AddRange(paid, expired, renewed, canceled);
        await db.SaveChangesAsync();
        var migration = new SeparateBonusProEntitlement();
        var up = migration.UpOperations.OfType<SqlOperation>().Single().Sql;
        var down = migration.DownOperations.OfType<SqlOperation>().Single().Sql;

        (await db.Database.ExecuteSqlRawAsync(up)).Should().Be(0);
        db.ChangeTracker.Clear();
        var rows = await db.Users.ToDictionaryAsync(u => u.Id);
        rows[paid.Id].HasActivePaidSubscription.Should().BeTrue();
        rows[paid.Id].PlanExpiresAt.Should().Be(paid.PlanExpiresAt);
        rows[expired.Id].HasProAccess.Should().BeFalse();
        rows[expired.Id].PlanExpiresAt.Should().Be(expired.PlanExpiresAt);
        rows[renewed.Id].HasActivePaidSubscription.Should().BeTrue();
        rows[renewed.Id].SubscriptionExpiresAtUtc.Should().Be(renewed.SubscriptionExpiresAtUtc);
        rows[canceled.Id].HasProAccess.Should().BeTrue();
        rows[canceled.Id].SubscriptionPlan.Should().Be(UserPlan.Free);

        (await db.Database.ExecuteSqlRawAsync(down)).Should().Be(1);
        db.ChangeTracker.Clear();
        rows = await db.Users.ToDictionaryAsync(u => u.Id);
        rows[paid.Id].SubscriptionExpiresAtUtc.Should().Be(paid.PlanExpiresAt);
        rows[expired.Id].SubscriptionExpiresAtUtc.Should().Be(expired.PlanExpiresAt);
        rows[renewed.Id].SubscriptionExpiresAtUtc.Should().Be(renewed.SubscriptionExpiresAtUtc);
        rows[canceled.Id].SubscriptionExpiresAtUtc.Should().Be(canceled.BonusProExpiresAtUtc);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
