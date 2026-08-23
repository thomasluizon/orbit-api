using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AiUsageSummaryServiceGenerationTests
{
    private static readonly DateOnly Yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    [Fact]
    public async Task SummarizeYesterdayAsync_WithRows_EmitsAggregatedSummary()
    {
        var harness = new Harness();
        await harness.SeedAsync(
            Row("chat", calls: 3, costUsd: 0.1000m),
            Row("daily_summary", calls: 2, costUsd: 0.0500m));

        await harness.Service.SummarizeYesterdayAsync(CancellationToken.None);

        var summary = harness.SingleSummaryLine();
        summary.Should().Contain($"AI cost {Yesterday:yyyy-MM-dd}: total=$0.1500 over 5 calls");
    }

    [Fact]
    public async Task SummarizeYesterdayAsync_NoRows_EmitsNoUsageRecorded()
    {
        var harness = new Harness();

        await harness.Service.SummarizeYesterdayAsync(CancellationToken.None);

        harness.SingleSummaryLine().Should().Be(
            $"AI cost {Yesterday:yyyy-MM-dd}: no usage recorded; " +
            $"Astra quota {Yesterday:yyyy-MM-dd}: no active users");
    }

    [Fact]
    public async Task SummarizeYesterdayAsync_CalledTwice_EmitsSummaryOnlyOnce()
    {
        var harness = new Harness();
        await harness.SeedAsync(Row("chat", calls: 1, costUsd: 0.2000m));

        await harness.Service.SummarizeYesterdayAsync(CancellationToken.None);
        await harness.Service.SummarizeYesterdayAsync(CancellationToken.None);

        harness.Logger.Entries.Count(entry => entry.Message.StartsWith("AI cost")).Should().Be(1);
    }

    [Fact]
    public async Task SummarizeYesterdayAsync_WithQuotaUsers_EmitsDailyQuotaMetrics()
    {
        var harness = new Harness();
        await harness.SeedUsersAsync(
            UserWithMessages(5, hasProAccess: false),
            UserWithMessages(50, hasProAccess: true));

        await harness.Service.SummarizeYesterdayAsync(CancellationToken.None);

        harness.SingleSummaryLine().Should().Contain(
            "free_cap_hits=1; pro_cap_hits=1; mean_messages_per_active_user=27.50; " +
            "p95_messages_per_active_user=50");
    }

    private static AiUsageDaily Row(string purpose, long calls, decimal costUsd) =>
        AiUsageDaily.Create(
            Yesterday, "gpt-4.1-mini", purpose,
            new AiUsageTotals(calls, CachedTokens: 0, PromptTokens: 0, CompletionTokens: 0, TotalTokens: 0, CostUsd: costUsd));

    private static User UserWithMessages(int count, bool hasProAccess)
    {
        var user = User.Create("Metrics User", $"metrics-{Guid.NewGuid():N}@example.com").Value;
        if (hasProAccess)
            user.GrantLifetimePro();
        else
            user.StartTrial(DateTime.UtcNow.AddDays(-1));

        for (var i = 0; i < count; i++)
            user.IncrementAiMessageCount(Yesterday);

        return user;
    }

    private sealed class Harness
    {
        private readonly string _databaseName = $"AiUsageSummary_{Guid.NewGuid()}";
        private readonly ServiceProvider _provider;

        public CapturingLogger Logger { get; } = new();

        public AiUsageSummaryService Service { get; }

        public Harness()
        {
            var appConfig = Substitute.For<IAppConfigService>();
            appConfig.GetAsync(AppConfigKeys.FreeAiMessagesPerDay, 5, Arg.Any<CancellationToken>()).Returns(5);
            appConfig.GetAsync(AppConfigKeys.ProAiMessagesPerDay, 50, Arg.Any<CancellationToken>()).Returns(50);
            _provider = new ServiceCollection()
                .AddDbContext<OrbitDbContext>(options => options.UseInMemoryDatabase(_databaseName))
                .AddSingleton(appConfig)
                .BuildServiceProvider();

            var settings = new AiSettings
            {
                Pricing =
                {
                    ["gpt-4.1-mini"] = new AiModelPrice
                    {
                        InputPerMillionUsd = 0.40m,
                        CachedInputPerMillionUsd = 0.10m,
                        OutputPerMillionUsd = 1.60m
                    }
                }
            };
            Service = new AiUsageSummaryService(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Logger,
                new ConfigurationBuilder().Build(),
                Options.Create(settings));
        }

        public async Task SeedAsync(params AiUsageDaily[] rows)
        {
            using var scope = _provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            dbContext.AiUsageDaily.AddRange(rows);
            await dbContext.SaveChangesAsync();
        }

        public async Task SeedUsersAsync(params User[] users)
        {
            using var scope = _provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            dbContext.Users.AddRange(users);
            await dbContext.SaveChangesAsync();
        }

        public string SingleSummaryLine() =>
            Logger.Entries.Single(entry => entry.Message.StartsWith("AI cost")).Message;
    }

    private sealed class CapturingLogger : ILogger<AiUsageSummaryService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
