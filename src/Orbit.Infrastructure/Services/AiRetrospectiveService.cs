using Microsoft.Extensions.Logging;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed class AiRetrospectiveService(
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

        logger.LogInformation(
            "Generating retrospective (period: {Period}, from: {From}, to: {To}, language: {Language})...",
            period, dateFrom, dateTo, language);

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

            logger.LogInformation("Retrospective generated successfully ({Length} chars)", trimmed.Length);
            return Result.Success(trimmed);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "AI API call failed for retrospective");
            return Result.Failure<string>($"AI API error: {ex.Message}");
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
            "pt-br" or "pt" => ("Destaques", "Oportunidades Perdidas", "Tendencias", "Sugestao"),
            _ => ("Highlights", "Missed Opportunities", "Trends", "Suggestion")
        };

        var totalDays = dateTo.DayNumber - dateFrom.DayNumber + 1;

        var habitLines = new List<string>();
        var totalCompletions = 0;
        var totalScheduled = 0;
        var badHabitSlips = 0;

        foreach (var habit in habits.Where(h => h.ParentHabitId is null))
        {
            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo);
            var scheduledCount = scheduledDates.Count;
            var logs = habit.Logs.Where(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value > 0).ToList();
            var completedCount = logs.Count;

            if (scheduledCount == 0 && completedCount == 0)
                continue;

            totalScheduled += scheduledCount;
            totalCompletions += completedCount;

            var rate = scheduledCount > 0 ? (int)Math.Round(100.0 * completedCount / scheduledCount) : 0;

            if (habit.IsBadHabit)
            {
                badHabitSlips += completedCount;
                habitLines.Add($"- {habit.Title} (bad habit): {completedCount} slips in {totalDays} days");
            }
            else
            {
                habitLines.Add($"- {habit.Title}: {completedCount}/{scheduledCount} completed ({rate}%)");
            }

            var children = habits.Where(h => h.ParentHabitId == habit.Id).ToList();
            foreach (var child in children)
            {
                var childLogs = child.Logs.Where(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value > 0).ToList();
                var childScheduled = HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo).Count;
                var childRate = childScheduled > 0 ? (int)Math.Round(100.0 * childLogs.Count / childScheduled) : 0;
                habitLines.Add($"  - {child.Title}: {childLogs.Count}/{childScheduled} ({childRate}%)");
            }
        }

        var habitSection = habitLines.Count > 0
            ? string.Join("\n", habitLines)
            : "(no habit activity)";

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
}
