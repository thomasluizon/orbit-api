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

        var prompt = BuildSummaryPrompt(scheduledHabits, dateFrom, dateTo, language, currentLocalTime);

        if (logger.IsEnabled(LogLevel.Information))
            LogGeneratingDailySummary(logger, dateFrom, language);

        try
        {
            var text = await aiClient.CompleteTextAsync(
                "You are Astra, a perceptive, warm close friend who knows the person well. You notice and celebrate what they have already done, and you stay easy and unpushy about what is left. You never sound corporate, clinical, or like a coach reading a checklist. You write plain text only -- no markdown, bullets, headings, emoji, or JSON -- with no greeting and no sign-off, only in the language you are told to use.",
                prompt,
                temperature: 0.7,
                cancellationToken,
                maxOutputTokens: 200);

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
        DateOnly date,
        DateOnly dateTo,
        string language,
        TimeOnly? currentLocalTime)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);

        var habitSection = BuildHabitSection(scheduledHabits, date, dateTo);

        var totalCount = scheduledHabits.Count;
        var doneTotal = scheduledHabits.Count(h => IsDoneInRange(h, date, dateTo));
        var timeContext = BuildTimeContext(currentLocalTime);

        return $"""
            Date: {date:MMMM d, yyyy}
            Current part of day: {timeContext}
            Progress: {doneTotal}/{totalCount} habits completed

            Today's habits:
            {habitSection}

            Write a short message to this person about their day.

            Rules:
            - LEAD with a specific, genuine acknowledgment of what they have ALREADY completed today -- name the activity naturally, don't just say "good job"
            - THEN, gently point at one or two of the still-pending habits as easy next moves -- never list everything, never frame it as a checklist, never guilt-trip
            - If nothing is done yet, stay warm and forward-looking; do NOT imply they are behind or failing
            - Describe the ACTIVITY naturally, don't just parrot the exact habit title
            - BAD: "You have Yoga, Morning Routine, and Guitar Playing left."
            - GOOD: "Nice work getting your run in -- some guitar later could be a great way to unwind."
            - Keep it to 2-3 sentences, warm and close, like a friend who actually knows you -- never corporate or coach-like
            - This message is shown for the WHOLE current part of the day, so it must read correctly whether they see it at the start or the end of that window
            - Treat the time of day as a broad window, not an exact moment; never imply a precise instant
            - Do NOT use phrases like "right now", "just woke up", "now that the afternoon is here", "as the day begins", "earlier today", or "upcoming later today"
            - Do NOT use markdown, bullet points, emojis, or JSON
            - Do NOT mention the date explicitly
            - Write ONLY in {languageName}
            - No greeting like "good morning", no sign-off -- just the message
            """;
    }

    private static string BuildHabitSection(
        List<Habit> scheduledHabits,
        DateOnly dateFrom,
        DateOnly dateTo)
    {
        var habitLines = new List<string>();

        foreach (var habit in scheduledHabits.Where(h => h.ParentHabitId is null))
        {
            var status = IsDoneInRange(habit, dateFrom, dateTo) ? "done" : "pending";
            var timing = DescribeTiming(habit);
            var children = scheduledHabits.Where(h => h.ParentHabitId == habit.Id).ToList();

            if (children.Count > 0)
            {
                var doneCount = children.Count(c => IsDoneInRange(c, dateFrom, dateTo));
                habitLines.Add($"- {habit.Title} ({status}, {doneCount}/{children.Count} sub-tasks done) [{timing}]");
                foreach (var child in children)
                    habitLines.Add($"  - {child.Title} ({(IsDoneInRange(child, dateFrom, dateTo) ? "done" : "pending")}) [{DescribeTiming(child)}]");
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

    private static string BuildTimeContext(TimeOnly? currentLocalTime)
    {
        if (!currentLocalTime.HasValue)
            return "not provided";

        var period = ResolveDayPeriod(currentLocalTime.Value);
        return $"{period.ToString().ToLowerInvariant()} ({PeriodRange(period)})";
    }

    private static string PeriodRange(DayPeriod period) => period switch
    {
        DayPeriod.Morning => "~5am-11am",
        DayPeriod.Afternoon => "~11am-5pm",
        DayPeriod.Evening => "~5pm-9pm",
        DayPeriod.Night => "~9pm-late",
        _ => "~5am-11am"
    };

    private static string DescribeTiming(Habit habit)
    {
        var dueDescription = habit.DueTime.HasValue
            ? $"due {habit.DueTime.Value:HH\\:mm}"
            : InferTitleTimePeriod(habit.Title);

        return dueDescription ?? "no specific time";
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
