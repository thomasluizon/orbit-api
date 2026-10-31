using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AiUsageSummaryServiceTests
{
    private static readonly DateOnly Date = new(2026, 6, 29);

    [Theory]
    [InlineData(1, 0, "2026-08-03", "2026-08-16")]
    [InlineData(0, 1, "2026-08-02", "2026-08-10")]
    public void WeekStartPreferenceChange_QuotaSummaryKeepsCapHitUntilAlignedBoundary(int originalStart, int newStart, string start, string end)
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        user.SetWeekStartDay(originalStart).IsSuccess.Should().BeTrue();
        var weekStart = DateOnly.Parse(start);
        var periodEnd = DateOnly.Parse(end);
        for (var message = 0; message < 250; message++)
            user.IncrementAiMessageCount(weekStart.AddDays(message * 7 / 250));
        user.SetWeekStartDay(newStart).IsSuccess.Should().BeTrue();

        for (var day = 6; day < periodEnd.DayNumber - weekStart.DayNumber; day++)
            AiUsageSummaryService.BuildQuotaSummaryLine(weekStart.AddDays(day), [user], 5, 250)
                .Should().Contain("pro_cap_hits=1");
        AiUsageSummaryService.BuildQuotaSummaryLine(periodEnd, [user], 5, 250)
            .Should().Contain("pro_cap_hits=0");
    }

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
        line.Should().Contain("chat=$0.1000 calls=3 cost_per_call=$0.033333 uncached_input=0 cached_input=0 output=0 reasoning=0");
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

        line.IndexOf("daily_summary=$", StringComparison.Ordinal).Should().BeLessThan(line.IndexOf("fact_extraction=$", StringComparison.Ordinal));
        line.IndexOf("fact_extraction=$", StringComparison.Ordinal).Should().BeLessThan(line.IndexOf("chat=$", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSummaryLine_IncludesEveryPurpose()
    {
        var rows = new List<AiUsageDaily>
        {
            Row("a", "gpt-4.1-mini", calls: 1, costUsd: 0.4000m),
            Row("b", "gpt-4.1-mini", calls: 1, costUsd: 0.3000m),
            Row("c", "gpt-4.1-mini", calls: 1, costUsd: 0.2000m),
            Row("d", "gpt-4.1-mini", calls: 1, costUsd: 0.1000m)
        };

        var line = AiUsageSummaryService.BuildSummaryLine(Date, rows, Pricing);

        line.Should().Contain("d=$0.1000 calls=1");
    }

    [Fact]
    public void BuildSummaryLine_AggregatesTokensAcrossModelsAndSeparatesEval()
    {
        var rows = new[]
        {
            AiUsageDaily.Create(Date, "gpt-4.1-mini", "chat", new(2, 600, 1000, 300, 1300, 0.002m, 100)),
            AiUsageDaily.Create(Date, "gpt-6-luna", "chat", new(2, 400, 1000, 500, 1500, 0.002m, 200)),
            AiUsageDaily.Create(Date, "gpt-6-luna", "eval_chat", new(1, 0, 100, 50, 150, 0.001m, 30))
        };

        var line = AiUsageSummaryService.BuildSummaryLine(Date, rows, Pricing);

        line.Should().Contain("chat=$0.0040 calls=4 cost_per_call=$0.001000 uncached_input=1000 cached_input=1000 output=800 reasoning=300 uncached_per_call=250.0 cached_per_call=250.0 output_per_call=200.0 reasoning_per_call=75.0");
        line.Should().Contain("eval_chat=$0.0010 calls=1 cost_per_call=$0.001000 uncached_input=100 cached_input=0 output=50 reasoning=30");
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
