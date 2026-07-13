using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Emits exactly one Information line per day summarising the previous UTC day's AI dollar cost, call
/// count, and top purposes from the <see cref="AiUsageDaily"/> aggregate. An in-memory marker keeps it
/// idempotent across the hourly tick; a process restart re-emits the prior day's line once.
/// </summary>
public partial class AiUsageSummaryService(
    IServiceScopeFactory scopeFactory,
    ILogger<AiUsageSummaryService> logger,
    IConfiguration configuration,
    IOptions<AiSettings> aiOptions) : ScheduledServiceBase, IScheduledJob
{
    private const int TopPurposeCount = 3;

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:AiUsageSummaryIntervalMinutes", 60));

    private readonly IReadOnlyDictionary<string, AiModelPrice> _pricing = aiOptions.Value.Pricing;

    private DateOnly? _lastSummarizedDate;

    public string Name => "ai-usage-summary";

    public string CronExpression => "5 0 * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await SummarizeYesterdayAsync(stoppingToken);
        BackgroundServiceHealthCheck.RecordTick("AiUsageSummary");
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task SummarizeYesterdayAsync(CancellationToken cancellationToken)
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        if (_lastSummarizedDate == yesterday)
            return;

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var rows = await dbContext.AiUsageDaily
            .AsNoTracking()
            .Where(usage => usage.Date == yesterday)
            .ToListAsync(cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
        {
            var summaryLine = BuildSummaryLine(yesterday, rows, _pricing);
            LogAiUsageSummary(logger, summaryLine);
        }
        _lastSummarizedDate = yesterday;
    }

    /// <summary>
    /// Builds the single human-readable cost line for a day: total cost and calls, the top purposes by
    /// cost, and a trailing list of any models that appeared but have no configured price (cost = $0).
    /// </summary>
    internal static string BuildSummaryLine(
        DateOnly date,
        IReadOnlyList<AiUsageDaily> rows,
        IReadOnlyDictionary<string, AiModelPrice> pricing)
    {
        if (rows.Count == 0)
            return $"AI cost {date:yyyy-MM-dd}: no usage recorded";

        var totalCostUsd = rows.Sum(row => row.CostUsd);
        var totalCalls = rows.Sum(row => row.Calls);

        var topPurposes = rows
            .GroupBy(row => row.Purpose)
            .Select(group => (Purpose: group.Key, Cost: group.Sum(row => row.CostUsd)))
            .OrderByDescending(entry => entry.Cost)
            .Take(TopPurposeCount)
            .Select(entry => $"{entry.Purpose}=${entry.Cost:F4}");

        var line = $"AI cost {date:yyyy-MM-dd}: total=${totalCostUsd:F4} over {totalCalls} calls; top: {string.Join(", ", topPurposes)}";

        var unpricedModels = rows
            .Select(row => row.Model)
            .Distinct()
            .Where(model => !pricing.ContainsKey(model))
            .OrderBy(model => model, StringComparer.Ordinal)
            .ToList();

        if (unpricedModels.Count > 0)
            line += $"; unpriced: {string.Join(", ", unpricedModels)}";

        return line;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "AiUsageSummaryService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "AiUsageSummaryService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in AI usage summary service")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "{AiUsageSummary}")]
    private static partial void LogAiUsageSummary(ILogger logger, string aiUsageSummary);
}
