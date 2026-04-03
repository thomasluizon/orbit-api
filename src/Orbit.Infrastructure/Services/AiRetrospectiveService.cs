using Microsoft.Extensions.Logging;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiRetrospectiveService(
    AiCompletionClient aiClient,
    ILogger<AiRetrospectiveService> logger) : IRetrospectiveService
{
    public async Task<Result<string>> GenerateRetrospectiveAsync(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        string period,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (habits.Count == 0)
            return Result.Failure<string>("No habits found for this period.");

        var prompt = BuildRetrospectivePrompt(habits, dateFrom, dateTo, period, language);

        if (logger.IsEnabled(LogLevel.Information))
            LogGeneratingRetrospective(logger, period, dateFrom, dateTo, language);

        try
        {
            var text = await aiClient.CompleteTextAsync(
                "You are a thoughtful habit coach writing retrospective reviews.",
                prompt,
                temperature: 0.7,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<string>("AI returned empty response");

            var trimmed = AiSummaryService.StripMarkdownFences(text);

            if (logger.IsEnabled(LogLevel.Information))
                LogRetrospectiveGenerated(logger, trimmed.Length);
            return Result.Success(trimmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRetrospectiveFailed(logger, ex);
            return Result.Failure<string>("AI retrospective temporarily unavailable");
        }
    }

    private static string BuildRetrospectivePrompt(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        string period,
        string language)
    {
        var languageName = language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };

        var (highlightsHeading, missedHeading, trendsHeading, suggestionHeading) = language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => ("Destaques", "Oportunidades Perdidas", "Tendências", "Sugestão"),
            _ => ("Highlights", "Missed Opportunities", "Trends", "Suggestion")
        };

        var totalDays = dateTo.DayNumber - dateFrom.DayNumber + 1;
        var (habitSection, totalCompletions, totalScheduled, badHabitSlips) =
            BuildHabitBreakdown(habits, dateFrom, dateTo, totalDays);

        var overallRate = totalScheduled > 0 ? (int)Math.Round(100.0 * totalCompletions / totalScheduled) : 0;

        return $"""
            Period: Last {totalDays} days ({period}) -- {dateFrom:MMMM d} to {dateTo:MMMM d, yyyy}
            Total habits tracked: {habits.Count(h => h.ParentHabitId is null)}
            Overall completion rate: {totalCompletions}/{totalScheduled} ({overallRate}%)
            {(badHabitSlips > 0 ? $"Bad habit slips: {badHabitSlips}" : "")}

            Per-habit breakdown:
            {habitSection}

            Write a retrospective with these sections (use these exact headings):
            1. **{highlightsHeading}** -- What went well, milestones reached, strong completion rates
            2. **{missedHeading}** -- What was skipped or neglected, patterns of avoidance
            3. **{trendsHeading}** -- Improving or declining patterns, consistency observations
            4. **{suggestionHeading}** -- One specific, actionable tip for the next period

            Rules:
            - Be honest but encouraging -- celebrate real wins, acknowledge real gaps
            - Reference specific habit names and numbers from the data
            - Keep each section to 2-3 sentences max
            - If a habit has 100% completion, call it out as a win
            - If a habit has <50% completion, flag it kindly
            - For bad habits, fewer slips = good progress
            - Do NOT use emojis or JSON
            - Use markdown bold for section headings only
            - Write ONLY in {languageName}
            """;
    }

    private static (string HabitSection, int TotalCompletions, int TotalScheduled, int BadHabitSlips) BuildHabitBreakdown(
        List<Habit> habits, DateOnly dateFrom, DateOnly dateTo, int totalDays)
    {
        var habitLines = new List<string>();
        var totalCompletions = 0;
        var totalScheduled = 0;
        var badHabitSlips = 0;

        foreach (var habit in habits.Where(h => h.ParentHabitId is null))
        {
            var scheduledCount = HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo).Count;
            var completedCount = habit.Logs.Count(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value > 0);

            if (scheduledCount == 0 && completedCount == 0)
                continue;

            totalScheduled += scheduledCount;
            totalCompletions += completedCount;

            AppendParentHabitLine(habitLines, habit, scheduledCount, completedCount, totalDays, ref badHabitSlips);
            AppendChildHabitLines(habitLines, habits, habit.Id, dateFrom, dateTo);
        }

        var section = habitLines.Count > 0 ? string.Join("\n", habitLines) : "(no habit activity)";
        return (section, totalCompletions, totalScheduled, badHabitSlips);
    }

    private static void AppendParentHabitLine(
        List<string> lines, Habit habit, int scheduledCount, int completedCount, int totalDays, ref int badHabitSlips)
    {
        var rate = scheduledCount > 0 ? (int)Math.Round(100.0 * completedCount / scheduledCount) : 0;

        if (habit.IsBadHabit)
        {
            badHabitSlips += completedCount;
            lines.Add($"- {habit.Title} (bad habit): {completedCount} slips in {totalDays} days");
        }
        else
        {
            lines.Add($"- {habit.Title}: {completedCount}/{scheduledCount} completed ({rate}%)");
        }
    }

    private static void AppendChildHabitLines(
        List<string> lines, List<Habit> allHabits, Guid parentId, DateOnly dateFrom, DateOnly dateTo)
    {
        foreach (var child in allHabits.Where(h => h.ParentHabitId == parentId))
        {
            var childLogs = child.Logs.Count(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value > 0);
            var childScheduled = HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo).Count;
            var childRate = childScheduled > 0 ? (int)Math.Round(100.0 * childLogs / childScheduled) : 0;
            lines.Add($"  - {child.Title}: {childLogs}/{childScheduled} ({childRate}%)");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Generating retrospective (period: {Period}, from: {From}, to: {To}, language: {Language})...")]
    private static partial void LogGeneratingRetrospective(ILogger logger, string period, DateOnly from, DateOnly to, string language);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Retrospective generated successfully ({Length} chars)")]
    private static partial void LogRetrospectiveGenerated(ILogger logger, int length);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "AI API call failed for retrospective")]
    private static partial void LogRetrospectiveFailed(ILogger logger, Exception ex);

}
