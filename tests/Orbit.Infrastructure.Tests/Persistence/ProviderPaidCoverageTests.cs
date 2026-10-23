using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ProviderPaidCoverageTests
{
    [Fact]
    public void Migration_AddsNullableCoverageAndBackfillsSelectedProvider()
    {
        var migration = new PreserveProviderPaidCoverage();
        migration.UpOperations.OfType<AddColumnOperation>().Should().HaveCount(9)
            .And.OnlyContain(column => column.Table == "Users" && column.IsNullable);
        migration.UpOperations.OfType<SqlOperation>().Should().HaveCount(3)
            .And.OnlyContain(operation => operation.Sql.Contains("IS NULL", StringComparison.Ordinal)
                && operation.Sql.Contains("\"PlanExpiresAt\"", StringComparison.Ordinal));
        var user = new OrbitDbContextModelSnapshot().Model.FindEntityType(typeof(User).FullName!)!;
        foreach (var column in migration.UpOperations.OfType<AddColumnOperation>())
            user.FindProperty(column.Name)!.IsNullable.Should().BeTrue();
    }

    [Theory]
    [InlineData(SubscriptionSource.Stripe)]
    [InlineData(SubscriptionSource.GooglePlay)]
    [InlineData(SubscriptionSource.AppStore)]
    public async Task Migration_BackfillsExistingPaidSourceAndCanRepeat(SubscriptionSource source)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var user = User.Create("Paid", "paid@example.com").Value;
        var now = DateTime.UtcNow;
        if (source == SubscriptionSource.Stripe)
            user.SetStripeSubscription("stripe", now.AddDays(30), SubscriptionInterval.Monthly);
        else if (source == SubscriptionSource.GooglePlay)
            user.SetPlaySubscription("play", now.AddDays(30), SubscriptionInterval.Monthly);
        else
            user.ApplyAppStoreSubscription("apple", "Sandbox", now.AddDays(30), SubscriptionInterval.Monthly, now, true, null);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await context.Users.ExecuteUpdateAsync(update => update
            .SetProperty(u => u.StripeCoverageExpiresAtUtc, (DateTime?)null).SetProperty(u => u.StripeCoverageInterval, (SubscriptionInterval?)null)
            .SetProperty(u => u.PlayCoverageExpiresAtUtc, (DateTime?)null).SetProperty(u => u.PlayCoverageInterval, (SubscriptionInterval?)null)
            .SetProperty(u => u.AppStoreCoverageExpiresAtUtc, (DateTime?)null).SetProperty(u => u.AppStoreCoverageInterval, (SubscriptionInterval?)null));
        var migration = new PreserveProviderPaidCoverage();
        for (var run = 0; run < 2; run++)
            foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                await context.Database.ExecuteSqlRawAsync(operation.Sql);
        context.ChangeTracker.Clear();
        var reloaded = await context.Users.SingleAsync();
        var expiry = source switch
        {
            SubscriptionSource.Stripe => reloaded.StripeCoverageExpiresAtUtc,
            SubscriptionSource.GooglePlay => reloaded.PlayCoverageExpiresAtUtc,
            _ => reloaded.AppStoreCoverageExpiresAtUtc
        };
        expiry.Should().Be(reloaded.SubscriptionExpiresAtUtc);
        var interval = source switch
        {
            SubscriptionSource.Stripe => reloaded.StripeCoverageInterval,
            SubscriptionSource.GooglePlay => reloaded.PlayCoverageInterval,
            _ => reloaded.AppStoreCoverageInterval
        };
        interval.Should().Be(SubscriptionInterval.Monthly);
    }

    [Fact]
    public async Task ReloadedCoverage_RefundFallsBackToStillPaidPlayTerm()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var user = User.Create("Paid", "paid@example.com").Value;
        var now = DateTime.UtcNow;
        user.StartTrial(now.AddDays(-30));
        user.SetPlaySubscription("play", now.AddDays(30), SubscriptionInterval.Monthly, isInGracePeriod: true);
        user.ApplyAppStoreSubscription("apple", "Sandbox", now.AddDays(365), SubscriptionInterval.Yearly, now, true, null);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var reloaded = await context.Users.SingleAsync();
        reloaded.ApplyAppStoreSubscription("apple", "Sandbox", now.AddDays(365), SubscriptionInterval.Yearly,
            now.AddMinutes(1), false, SubscriptionLapseReason.Expired);
        reloaded.HasProAccess.Should().BeTrue();
        reloaded.SubscriptionSource.Should().Be(SubscriptionSource.GooglePlay);
        reloaded.SubscriptionInterval.Should().Be(SubscriptionInterval.Monthly);
        reloaded.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.PaymentFailed);
        reloaded.PlayPurchaseToken.Should().Be("play");
    }
}
