using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
    }
}
