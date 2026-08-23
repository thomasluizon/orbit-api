using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AiUsageSummaryServiceTests
{
    private static readonly DateOnly Date = new(2026, 6, 29);

    private static readonly IReadOnlyDictionary<string, AiModelPrice> Pricing =
        new Dictionary<string, AiModelPrice>
        {
            ["gpt-4.1-mini"] = new()
            {
                InputPerMillionUsd = 0.40m,
                CachedInputPerMillionUsd = 0.10m,
                OutputPerMillionUsd = 1.60m
            }
        };

    [Fact]
    public void BuildSummaryLine_NoRows_ReportsNoUsage()
    {
        var line = AiUsageSummaryService.BuildSummaryLine(Date, [], Pricing);

        line.Should().Be("AI cost 2026-06-29: no usage recorded");
    }

    [Fact]
    public void BuildSummaryLine_AggregatesTotalCostAndCalls()
    {
        var rows = new List<AiUsageDaily>
        {
            Row("chat", "gpt-4.1-mini", calls: 3, costUsd: 0.1000m),
            Row("daily_summary", "gpt-4.1-mini", calls: 2, costUsd: 0.0500m)
        };

        var line = AiUsageSummaryService.BuildSummaryLine(Date, rows, Pricing);

        line.Should().StartWith("AI cost 2026-06-29: total=$0.1500 over 5 calls;");
    }

    [Fact]
    public void BuildSummaryLine_OrdersTopPurposesByCostDescending()
    {
        var rows = new List<AiUsageDaily>
        {
            Row("chat", "gpt-4.1-mini", calls: 1, costUsd: 0.0200m),
            Row("daily_summary", "gpt-4.1-mini", calls: 1, costUsd: 0.5000m),
            Row("fact_extraction", "gpt-4.1-mini", calls: 1, costUsd: 0.1000m)
        };

        var line = AiUsageSummaryService.BuildSummaryLine(Date, rows, Pricing);

        line.Should().Contain("top: daily_summary=$0.5000, fact_extraction=$0.1000, chat=$0.0200");
    }

    [Fact]
    public void BuildSummaryLine_TakesAtMostThreePurposes()
    {
        var rows = new List<AiUsageDaily>
        {
            Row("a", "gpt-4.1-mini", calls: 1, costUsd: 0.4000m),
            Row("b", "gpt-4.1-mini", calls: 1, costUsd: 0.3000m),
            Row("c", "gpt-4.1-mini", calls: 1, costUsd: 0.2000m),
            Row("d", "gpt-4.1-mini", calls: 1, costUsd: 0.1000m)
        };

        var line = AiUsageSummaryService.BuildSummaryLine(Date, rows, Pricing);

        line.Should().Contain("top: a=$0.4000, b=$0.3000, c=$0.2000");
        line.Should().NotContain("d=$");
    }

    [Fact]
    public void BuildSummaryLine_FlagsModelsWithoutConfiguredPrice()
    {
        var rows = new List<AiUsageDaily>
        {
            Row("chat", "gpt-4.1-mini", calls: 1, costUsd: 0.1000m),
            Row("chat", "gpt-5.4-nano", calls: 1, costUsd: 0m)
        };

        var line = AiUsageSummaryService.BuildSummaryLine(Date, rows, Pricing);

        line.Should().EndWith("; unpriced: gpt-5.4-nano");
    }

    [Fact]
    public void BuildQuotaSummaryLine_ReportsCapHitsMeanAndP95()
    {
        var users = new List<User>
        {
            UserWithMessages(5, hasProAccess: false),
            UserWithMessages(2, hasProAccess: false),
            UserWithMessages(50, hasProAccess: true),
            UserWithMessages(10, hasProAccess: true)
        };

        var line = AiUsageSummaryService.BuildQuotaSummaryLine(Date, users, freeLimit: 5, proLimit: 50);

        line.Should().Be(
            "Astra quota 2026-06-29: free_cap_hits=1; pro_cap_hits=1; " +
            "mean_messages_per_active_user=16.75; p95_messages_per_active_user=50");
    }

    [Fact]
    public void BuildQuotaSummaryLine_NoActiveUsers_ReportsEmptyPopulation()
    {
        var line = AiUsageSummaryService.BuildQuotaSummaryLine(Date, [], freeLimit: 5, proLimit: 50);

        line.Should().Be("Astra quota 2026-06-29: no active users");
    }

    private static AiUsageDaily Row(string purpose, string model, long calls, decimal costUsd) =>
        AiUsageDaily.Create(
            Date, model, purpose,
            new AiUsageTotals(calls, CachedTokens: 0, PromptTokens: 0, CompletionTokens: 0, TotalTokens: 0, CostUsd: costUsd));

    private static User UserWithMessages(int count, bool hasProAccess)
    {
        var user = User.Create("Metrics User", $"metrics-{Guid.NewGuid():N}@example.com").Value;
        if (hasProAccess)
            user.GrantLifetimePro();
        else
            user.StartTrial(DateTime.UtcNow.AddDays(-1));

        for (var i = 0; i < count; i++)
            user.IncrementAiMessageCount(Date);

        return user;
    }
}
