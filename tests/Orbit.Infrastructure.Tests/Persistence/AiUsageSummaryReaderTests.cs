using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class AiUsageSummaryReaderTests
{
    [Fact]
    public async Task Read_ThousandUsageRowsAndUsers_AggregatesMatchReferenceWithoutFullPopulationReads()
    {
        var commands = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var db = factory.Context;
        var date = new DateOnly(2030, 6, 15);
        var users = new List<User>();
        var usage = new List<AiUsageDaily>();
        for (var index = 0; index < 1000; index++)
        {
            var user = User.Create($"User {index}", $"u{index}@example.com").Value;
            user.StartTrial(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            if (index % 3 == 0) user.GrantLifetimePro();
            for (var message = 0; message <= index % 250; message++) user.IncrementAiMessageCount(date);
            users.Add(user);
            usage.Add(AiUsageDaily.Create(date, index % 2 == 0 ? "priced" : "unknown", index % 3 == 0 ? "chat" : "summary",
                new(2, 5, 10, 10, 20, .001m, 3), user.Id));
        }
        db.AddRange(users);
        db.AddRange(usage);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        commands.Reset();

        var aggregateUsage = await AiUsageSummaryReader.ReadUsageAsync(db, date, default);
        var aggregateQuota = await AiUsageSummaryReader.ReadQuotaAsync(db, date, date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), 5, 250, default);

        var prices = new Dictionary<string, AiModelPrice> { ["priced"] = new() };
        AiUsageSummaryService.BuildSummaryLine(date, aggregateUsage, prices)
            .Should().Be(AiUsageSummaryService.BuildSummaryLine(date, usage, prices));
        AiUsageSummaryService.BuildQuotaSummaryLine(date, aggregateQuota)
            .Should().Be(AiUsageSummaryService.BuildQuotaSummaryLine(date, users, 5, 250));
        aggregateUsage.Should().HaveCount(4);
        commands.SelectCommandCount.Should().Be(2);
        commands.Commands.Should().OnlyContain(command => command.Sql.Contains("GROUP BY", StringComparison.Ordinal) && command.Rows <= 500);
    }

    [Fact]
    public void PostgresQuota_UsesServerPercentileAndExtendedWeekWindow()
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=query_shape;Username=query_shape").Options);
        var sql = AiUsageSummaryReader.BuildPostgresQuotaQuery(db, new DateOnly(2030, 6, 15),
            new DateTime(2030, 6, 15, 12, 0, 0, DateTimeKind.Utc), 5, 250).ToQueryString();
        sql.Should().Contain("percentile_disc(0.95) WITHIN GROUP").And.Contain("COUNT(*) FILTER")
            .And.Contain("EXTRACT(DOW").And.Contain("AiMessagesWeekStart").And.Contain("IsDeactivated");
        var user = db.Model.FindEntityType(typeof(User))!;
        var table = StoreObjectIdentifier.Table(user.GetTableName()!, user.GetSchema());
        foreach (var property in new[] { nameof(User.SubscriptionPlan), nameof(User.SubscriptionExpiresAtUtc) })
            sql.Should().Contain($"\"{user.FindProperty(property)!.GetColumnName(table)}\"");
    }

    [PostgresFact]
    public async Task PostgresQuota_ExecutesMappedSubscriptionColumnsAndMatchesReference()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            var date = new DateOnly(2030, 6, 15);
            var nowAtUtc = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
            var users = Enumerable.Range(0, 6).Select(index =>
            {
                var user = User.Create($"Quota {index}", $"quota{index}@example.com").Value;
                user.StartTrial(nowAtUtc.AddYears(-1));
                if (index == 1) user.SetStripeSubscription("paid", nowAtUtc.AddDays(1));
                if (index == 2) user.SetStripeSubscription("expired", nowAtUtc.AddDays(-1));
                if (index == 3) user.GrantLifetimePro();
                user.SetWeekStartDay(1).IsSuccess.Should().BeTrue();
                for (var message = 0; message < 5 + index; message++) user.IncrementAiMessageCount(index == 5 ? date.AddDays(-1) : date);
                if (index == 4) user.Deactivate(nowAtUtc.AddDays(30));
                return user;
            }).ToList();
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var quota = await AiUsageSummaryReader.ReadQuotaAsync(db, date, nowAtUtc, 5, 5, default);

            quota.ActiveUsers.Should().Be(4);
            quota.FreeCapHits.Should().Be(2);
            quota.ProCapHits.Should().Be(2);
            quota.Mean.Should().Be(6.5);
            quota.P95.Should().Be(8);
        });
    }
}
