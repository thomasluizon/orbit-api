using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiSummaryService(
    AiCompletionClient aiClient,
    ILogger<AiSummaryService> logger) : ISummaryService
{
    public async Task<Result<string>> GenerateSummaryAsync(
        IEnumerable<Habit> allHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeOverdue,
        string language,
        CancellationToken cancellationToken = default)
    {
        var habitList = allHabits.ToList();

        var scheduledTopLevel = habitList
            .Where(h => h.ParentHabitId is null
                         && HabitScheduleService.GetScheduledDates(h, dateFrom, dateTo).Count > 0)
            .ToList();

        var scheduledTopLevelIds = scheduledTopLevel.Select(h => h.Id).ToHashSet();

        var children = habitList
            .Where(h => h.ParentHabitId is not null && scheduledTopLevelIds.Contains(h.ParentHabitId.Value))
            .ToList();

        var scheduledHabits = scheduledTopLevel.Concat(children).ToList();

        var overdueHabits = includeOverdue
            ? habitList.Where(h => !h.IsCompleted && h.DueDate < dateFrom).ToList()
            : [];

        var prompt = BuildSummaryPrompt(scheduledHabits, overdueHabits, dateFrom, language);

        if (logger.IsEnabled(LogLevel.Information))
            LogGeneratingDailySummary(logger, dateFrom, language);

        try
        {
            var text = await aiClient.CompleteTextAsync(
                "You are a friendly habit coach. Write short daily briefings.",
                prompt,
                temperature: 0.7,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<string>("AI returned empty response");

            var trimmed = StripMarkdownFences(text);

            if (logger.IsEnabled(LogLevel.Information))
                LogDailySummaryGenerated(logger, trimmed.Length);
            return Result.Success(trimmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDailySummaryFailed(logger, ex);
            return Result.Failure<string>("AI summary temporarily unavailable");
        }
    }

    private static string BuildSummaryPrompt(
        List<Habit> scheduledHabits,
        List<Habit> overdueHabits,
        DateOnly date,
        string language)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);

        var habitSection = BuildHabitSection(scheduledHabits);

        var overdueSection = overdueHabits.Count > 0
            ? string.Join("\n", overdueHabits.Select(h => $"- {h.Title}"))
            : "(none)";

        var totalCount = scheduledHabits.Count;
        var doneTotal = scheduledHabits.Count(h => h.IsCompleted);

        return $"""
            Date: {date:MMMM d, yyyy}
            Progress: {doneTotal}/{totalCount} habits completed

            Today's habits:
            {habitSection}

            Overdue from previous days:
            {overdueSection}

            Rules:
            - Write 2-3 short sentences max, like a supportive friend texting you
            - Weave habits into natural sentences about the DAY, don't just list habit names
            - BAD: "Today you have Yoga, Morning Routine, and Guitar Playing."
            - GOOD: "A good day to stretch out with some yoga and get creative on the guitar."
            - Describe the ACTIVITY naturally, don't just parrot the exact habit title
            - If some habits are done, briefly acknowledge progress
            - If there are overdue habits, gently nudge without guilt-tripping
            - Keep it casual, warm, and concise -- not corporate or overly enthusiastic
            - Do NOT use markdown, bullet points, emojis, or JSON
            - Do NOT mention the date explicitly
            - Write ONLY in {languageName}
            - No greeting like "good morning", no sign-off -- just the briefing
            """;
    }

    private static string BuildHabitSection(List<Habit> scheduledHabits)
    {
        var habitLines = new List<string>();

        foreach (var habit in scheduledHabits.Where(h => h.ParentHabitId is null))
        {
            var status = habit.IsCompleted ? "done" : "pending";
            var children = scheduledHabits.Where(h => h.ParentHabitId == habit.Id).ToList();

            if (children.Count > 0)
            {
                var doneCount = children.Count(c => c.IsCompleted);
                habitLines.Add($"- {habit.Title} ({status}, {doneCount}/{children.Count} sub-tasks done)");
                foreach (var child in children)
                    habitLines.Add($"  - {child.Title} ({(child.IsCompleted ? "done" : "pending")})");
            }
            else
            {
                habitLines.Add($"- {habit.Title} ({status})");
            }
        }

        return habitLines.Count > 0 ? string.Join("\n", habitLines) : "(no habits scheduled)";
    }

    internal static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var lines = trimmed.Split('\n');
            if (lines.Length >= 2)
                trimmed = string.Join('\n', lines.Skip(1).Take(lines.Length - (lines[^1].TrimStart().StartsWith("```") ? 2 : 1)));
        }
        return trimmed.Trim();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Generating daily summary (date: {Date}, language: {Language})...")]
    private static partial void LogGeneratingDailySummary(ILogger logger, DateOnly date, string language);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Daily summary generated successfully ({Length} chars)")]
    private static partial void LogDailySummaryGenerated(ILogger logger, int length);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "AI API call failed for daily summary")]
    private static partial void LogDailySummaryFailed(ILogger logger, Exception ex);

}
