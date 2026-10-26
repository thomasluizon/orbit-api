using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class ReadBlockTelemetry
{
    public static void Capture(IProductAnalytics analytics, ILogger logger, Guid userId,
        string eventName, ReadBlockMetadata read, string kind, string? metric = null, string outcome = "succeeded") =>
        AnalyticsCapture.SafeCaptureUserEvent(analytics, logger, userId, null, eventName,
            new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["query_family"] = ReadBlockFactory.Supports(read.QueryFamily) ? read.QueryFamily : "unknown",
                ["metric"] = metric is not null && PeriodAnalyticsCalculator.Metrics.Contains(metric) ? metric : "none",
                ["completeness"] = read.Completeness,
                ["outcome"] = outcome
            });

    public static (ReadBlockMetadata? Read, string Kind, string? Metric) Describe(object block) => block switch
    {
        HabitListCard card => (card.Read, "habitList", null),
        GoalListCard card => (card.Read, "goalList", null),
        MetricsCard card => (card.Read, "metrics", null),
        PeriodInsightCard card => (card.Read, "periodInsight", null),
        DaySummaryCard card => (card.Read, "daySummary", null),
        StreakCard card => (card.Read, "streak", null),
        GamificationCard card => (card.Read, "gamification", null),
        HabitMonthCard card => (card.Read, "habitMonth", null),
        CalendarCard card => (card.Read, "calendar", null),
        RecordListCard card => (card.Read, "recordList", null),
        AccountRowsCard card => (card.Read, "accountRows", null),
        PeriodTotalCard card => (card.Read, "periodTotal", card.Metric),
        PeriodComparisonCard card => (card.Read, "periodComparison", card.Metric),
        _ => (null, "unknown", null)
    };
}
