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
        TimeOnly? currentLocalTime,
        CancellationToken cancellationToken = default)
    {
        var habitList = allHabits.ToList();

        var scheduledTopLevel = habitList
            .Where(h => h.ParentHabitId is null
                         && !HasSkipLogInRange(h, dateFrom, dateTo)
                         && (HabitScheduleService.GetScheduledDates(h, dateFrom, dateTo).Count > 0
                             || HasCompletedLogInRange(h, dateFrom, dateTo)))
            .ToList();

        var scheduledTopLevelIds = scheduledTopLevel.Select(h => h.Id).ToHashSet();

        var children = habitList
            .Where(h => h.ParentHabitId is not null
                        && scheduledTopLevelIds.Contains(h.ParentHabitId.Value)
                        && !HasSkipLogInRange(h, dateFrom, dateTo))
            .ToList();

        var scheduledHabits = scheduledTopLevel.Concat(children).ToList();

        var overdueHabits = includeOverdue
            ? habitList
                .Where(h => !h.IsCompleted
                            && h.DueDate < dateFrom
                            && !HasSkipLogInRange(h, dateFrom, dateTo))
                .ToList()
            : [];

        var prompt = BuildSummaryPrompt(scheduledHabits, overdueHabits, dateFrom, dateTo, language, currentLocalTime);

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
        DateOnly dateTo,
        string language,
        TimeOnly? currentLocalTime)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);

        var habitSection = BuildHabitSection(scheduledHabits, date, dateTo, currentLocalTime);

        var overdueSection = overdueHabits.Count > 0
            ? string.Join("\n", overdueHabits.Select(h => $"- {h.Title}"))
            : "(none)";

        var totalCount = scheduledHabits.Count;
        var doneTotal = scheduledHabits.Count(h => IsDoneInRange(h, date, dateTo));
        var timeContext = BuildTimeContext(currentLocalTime);

        return $"""
            Date: {date:MMMM d, yyyy}
            Current local time: {timeContext}
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
            - Use the current local time to decide what is still relevant now
            - If it is evening or night, do NOT frame earlier morning habits as a way to start the day
            - When earlier-day habits are still pending, mention them only as optional catch-up or closure, then focus on habits that fit the current or upcoming part of the day
            - Keep it casual, warm, and concise -- not corporate or overly enthusiastic
            - Do NOT use markdown, bullet points, emojis, or JSON
            - Do NOT mention the date explicitly
            - Write ONLY in {languageName}
            - No greeting like "good morning", no sign-off -- just the briefing
            """;
    }

    private static string BuildHabitSection(
        List<Habit> scheduledHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        TimeOnly? currentLocalTime)
    {
        var habitLines = new List<string>();

        foreach (var habit in scheduledHabits.Where(h => h.ParentHabitId is null))
        {
            var status = IsDoneInRange(habit, dateFrom, dateTo) ? "done" : "pending";
            var timing = DescribeTiming(habit, currentLocalTime);
            var children = scheduledHabits.Where(h => h.ParentHabitId == habit.Id).ToList();

            if (children.Count > 0)
            {
                var doneCount = children.Count(c => IsDoneInRange(c, dateFrom, dateTo));
                habitLines.Add($"- {habit.Title} ({status}, {doneCount}/{children.Count} sub-tasks done) [{timing}]");
                foreach (var child in children)
                    habitLines.Add($"  - {child.Title} ({(IsDoneInRange(child, dateFrom, dateTo) ? "done" : "pending")}) [{DescribeTiming(child, currentLocalTime)}]");
            }
            else
            {
                habitLines.Add($"- {habit.Title} ({status}) [{timing}]");
            }
        }

        return habitLines.Count > 0 ? string.Join("\n", habitLines) : "(no habits scheduled)";
    }

    private static bool HasSkipLogInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        habit.Logs.Any(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value == 0);

    private static bool HasCompletedLogInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        habit.Logs.Any(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value > 0);

    private static bool IsDoneInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        habit.IsCompleted || HasCompletedLogInRange(habit, dateFrom, dateTo);

    private static string BuildTimeContext(TimeOnly? currentLocalTime) =>
        currentLocalTime.HasValue
            ? $"{currentLocalTime.Value:HH\\:mm} ({ResolveDayPeriod(currentLocalTime.Value)})"
            : "not provided";

    private static string DescribeTiming(Habit habit, TimeOnly? currentLocalTime)
    {
        var dueDescription = habit.DueTime.HasValue
            ? $"due {habit.DueTime.Value:HH\\:mm}"
            : InferTitleTimePeriod(habit.Title);

        if (!currentLocalTime.HasValue)
            return dueDescription ?? "no specific time";

        var relation = ResolveTimeRelation(habit, currentLocalTime.Value);
        return dueDescription is null ? relation : $"{dueDescription}, {relation}";
    }

    private static string ResolveTimeRelation(Habit habit, TimeOnly currentLocalTime)
    {
        if (habit.DueTime.HasValue)
            return habit.DueTime.Value < currentLocalTime ? "earlier today" : "upcoming later today";

        var inferredPeriod = InferTitleDayPeriod(habit.Title);
        if (inferredPeriod is null)
            return "no specific time";

        return PeriodRank(inferredPeriod.Value) < PeriodRank(ResolveDayPeriod(currentLocalTime))
            ? "earlier today"
            : "fits now or later today";
    }

    private static string? InferTitleTimePeriod(string title)
    {
        var period = InferTitleDayPeriod(title);
        return period is null ? null : $"title suggests {period.Value}";
    }

    private static DayPeriod? InferTitleDayPeriod(string title)
    {
        var normalized = title.ToLowerInvariant();
        if (normalized.Contains("morning", StringComparison.Ordinal)
            || normalized.Contains("matinal", StringComparison.Ordinal)
            || normalized.Contains("manhã", StringComparison.Ordinal)
            || normalized.Contains("manha", StringComparison.Ordinal))
            return DayPeriod.Morning;

        if (normalized.Contains("afternoon", StringComparison.Ordinal)
            || normalized.Contains("tarde", StringComparison.Ordinal))
            return DayPeriod.Afternoon;

        if (normalized.Contains("evening", StringComparison.Ordinal)
            || normalized.Contains("night", StringComparison.Ordinal)
            || normalized.Contains("noite", StringComparison.Ordinal)
            || normalized.Contains("noturno", StringComparison.Ordinal))
            return DayPeriod.Night;

        return null;
    }

    private static DayPeriod ResolveDayPeriod(TimeOnly time)
    {
        var hour = time.Hour;
        if (hour < 11) return DayPeriod.Morning;
        if (hour < 17) return DayPeriod.Afternoon;
        if (hour < 21) return DayPeriod.Evening;
        return DayPeriod.Night;
    }

    private static int PeriodRank(DayPeriod period) => period switch
    {
        DayPeriod.Morning => 0,
        DayPeriod.Afternoon => 1,
        DayPeriod.Evening => 2,
        DayPeriod.Night => 3,
        _ => 0
    };

    private enum DayPeriod
    {
        Morning,
        Afternoon,
        Evening,
        Night
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
