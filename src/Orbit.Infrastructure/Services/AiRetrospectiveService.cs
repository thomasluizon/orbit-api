using System.Text;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiRetrospectiveService(
    AiCompletionClient aiClient,
    ILogger<AiRetrospectiveService> logger) : IRetrospectiveService
{
    public async Task<Result<RetrospectiveNarrative>> GenerateRetrospectiveAsync(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        string period,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (habits.Count == 0)
            return Result.Failure<RetrospectiveNarrative>(ErrorMessages.NoHabitsForPeriod);

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
                return Result.Failure<RetrospectiveNarrative>(ErrorMessages.AiEmptyResponse);

            var trimmed = AiSummaryService.StripMarkdownFences(text);
            var narrative = ParseNarrative(trimmed, language);

            if (logger.IsEnabled(LogLevel.Information))
                LogRetrospectiveGenerated(logger, trimmed.Length);
            return Result.Success(narrative);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRetrospectiveFailed(logger, ex);
            return Result.Failure<RetrospectiveNarrative>(ErrorMessages.AiRetrospectiveUnavailable);
        }
    }

    private static (string Highlights, string Missed, string Trends, string Suggestion) GetHeadings(string language)
    {
        return LocaleHelper.IsPortuguese(language)
            ? ("Destaques", "Oportunidades Perdidas", "Tendências", "Sugestão")
            : ("Highlights", "Missed Opportunities", "Trends", "Suggestion");
    }

    /// <summary>
    /// Splits the AI document into its four sections by locating each known heading. When the four
    /// headings are not all found in order, the whole text is returned as <c>Highlights</c> so the
    /// caller always receives the model's output rather than an empty payload.
    /// </summary>
    private static RetrospectiveNarrative ParseNarrative(string text, string language)
    {
        var (highlights, missed, trends, suggestion) = GetHeadings(language);
        var order = new[] { highlights, missed, trends, suggestion };

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sections = new string[4];
        var buffer = new StringBuilder();
        var currentSection = -1;
        var matchedSections = 0;

        foreach (var line in lines)
        {
            var headingIndex = MatchHeadingIndex(line, order);
            if (headingIndex >= 0)
            {
                FlushSection(sections, currentSection, buffer);
                currentSection = headingIndex;
                matchedSections++;
                continue;
            }

            if (currentSection >= 0)
                buffer.AppendLine(line);
        }

        FlushSection(sections, currentSection, buffer);

        if (matchedSections < 4)
            return new RetrospectiveNarrative(text.Trim(), string.Empty, string.Empty, string.Empty);

        return new RetrospectiveNarrative(sections[0], sections[1], sections[2], sections[3]);
    }

    private static void FlushSection(string[] sections, int sectionIndex, StringBuilder buffer)
    {
        if (sectionIndex >= 0)
            sections[sectionIndex] = buffer.ToString().Trim();
        buffer.Clear();
    }

    private static int MatchHeadingIndex(string line, string[] headings)
    {
        var normalized = NormalizeHeadingLine(line);
        if (normalized.Length == 0)
            return -1;

        for (var i = 0; i < headings.Length; i++)
        {
            if (normalized.Equals(headings[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static string NormalizeHeadingLine(string line)
    {
        var trimmed = line.Trim().Trim('#', '*', ' ', '-', ':', '\t');

        var dotIndex = trimmed.IndexOf('.');
        if (dotIndex > 0 && dotIndex < trimmed.Length - 1 && int.TryParse(trimmed[..dotIndex], out _))
            trimmed = trimmed[(dotIndex + 1)..];

        var separatorIndex = trimmed.IndexOf("--", StringComparison.Ordinal);
        if (separatorIndex >= 0)
            trimmed = trimmed[..separatorIndex];

        return trimmed.Trim('#', '*', ' ', '-', ':', '\t');
    }

    private static string BuildRetrospectivePrompt(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        string period,
        string language)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);

        var (highlightsHeading, missedHeading, trendsHeading, suggestionHeading) = GetHeadings(language);

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
