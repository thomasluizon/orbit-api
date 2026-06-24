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
    private const int MaxSummaryChars = 300;

    public async Task<Result<string>> GenerateSummaryAsync(
        IEnumerable<Habit> allHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday,
        string language,
        TimeOnly? currentLocalTime,
        int currentStreak,
        int streakFreezesAccumulated,
        CancellationToken cancellationToken = default)
    {
        var scheduledHabits = SelectScheduledHabits(allHabits, userToday, dateFrom, dateTo);

        var prompt = BuildSummaryPrompt(
            scheduledHabits, dateFrom, dateTo, userToday, language, currentLocalTime, currentStreak, streakFreezesAccumulated);

        if (logger.IsEnabled(LogLevel.Information))
            LogGeneratingDailySummary(logger, dateFrom, language);

        try
        {
            var text = await aiClient.CompleteTextAsync(
                "You are Astra, a perceptive, warm close friend who knows the person well. You notice and celebrate the good things they have already done, you treat a slip on a habit they are trying to quit as a gentle, judgment-free moment rather than something to praise, and you stay easy and unpushy about what is left. You never sound corporate, clinical, or like a coach reading a checklist. You write plain text only -- no markdown, bullets, headings, emoji, or JSON -- with no greeting and no sign-off, only in the language you are told to use.",
                prompt,
                temperature: 0.7,
                cancellationToken,
                maxOutputTokens: 180);

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<string>(ErrorMessages.AiEmptyResponse);

            var trimmed = CapToSentence(StripMarkdownFences(text), MaxSummaryChars);

            if (logger.IsEnabled(LogLevel.Information))
                LogDailySummaryGenerated(logger, trimmed.Length);
            return Result.Success(trimmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDailySummaryFailed(logger, ex);
            return Result.Failure<string>(ErrorMessages.AiSummaryUnavailable);
        }
    }

    /// <summary>
    /// Selects the habits the summary should reason about: anything logged on the viewed day
    /// (completed today), plus anything still open — not completed and with a
    /// <see cref="Habit.DueDate"/> on or before the user's today (due today or overdue). Habits due
    /// only in the future, and tasks already completed on an earlier day, are excluded. Each child
    /// is evaluated on its own merit so a non-due child never rides in on a due parent.
    /// </summary>
    private static List<Habit> SelectScheduledHabits(
        IEnumerable<Habit> allHabits,
        DateOnly userToday,
        DateOnly dateFrom,
        DateOnly dateTo)
    {
        var habitList = allHabits.ToList();

        var scheduledTopLevel = habitList
            .Where(h => h.ParentHabitId is null
                         && !HasSkipLogInRange(h, dateFrom, dateTo)
                         && IsRelevant(h, dateFrom, dateTo, userToday))
            .ToList();

        var scheduledTopLevelIds = scheduledTopLevel.Select(h => h.Id).ToHashSet();

        var children = habitList
            .Where(h => h.ParentHabitId is not null
                        && scheduledTopLevelIds.Contains(h.ParentHabitId.Value)
                        && !HasSkipLogInRange(h, dateFrom, dateTo)
                        && IsRelevant(h, dateFrom, dateTo, userToday))
            .ToList();

        return scheduledTopLevel.Concat(children).ToList();
    }

    private static string BuildSummaryPrompt(
        List<Habit> scheduledHabits,
        DateOnly date,
        DateOnly dateTo,
        DateOnly userToday,
        string language,
        TimeOnly? currentLocalTime,
        int currentStreak,
        int streakFreezesAccumulated)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);

        var habitSection = BuildHabitSection(scheduledHabits, date, dateTo, userToday);

        var goodHabits = scheduledHabits.Where(h => h.ParentHabitId is null && !h.IsBadHabit).ToList();
        var doneTotal = goodHabits.Count(h => IsDoneInRange(h, date, dateTo));
        var badHabitSlips = scheduledHabits.Count(h => h.IsBadHabit && IsDoneInRange(h, date, dateTo));
        var contextHeader = BuildContextHeader(currentLocalTime, currentStreak, streakFreezesAccumulated, badHabitSlips);

        return $"""
            Date: {date:MMMM d, yyyy}
            {contextHeader}
            Progress: {doneTotal}/{goodHabits.Count} habits completed

            Today's habits:
            {habitSection}

            Write a short message to this person about their day.

            Rules:
            - LEAD with a specific, genuine acknowledgment of what they have ALREADY completed today -- name the activity naturally, don't just say "good job"
            - THEN, gently point at ONE still-pending habit as an easy next move -- never list everything, never frame it as a checklist, never guilt-trip
            - Lines marked "overdue" are the most worth a gentle nudge, but raise at most one and never with guilt
            - A line tagged "bad habit -- slipped" is a slip on something they are trying to QUIT: never congratulate it, never count it as a win; acknowledge it briefly and kindly, or simply focus elsewhere
            - A line tagged "bad habit -- clean" means they have stayed away from something they are trying to quit: THAT is the real win worth naming warmly; for these, fewer slips and longer clean streaks are the progress
            - If EVERYTHING is already done (nothing is pending), simply celebrate the full day warmly and leave it there -- do NOT invent, imply, or suggest any remaining task
            - If nothing is done yet, stay warm and forward-looking; do NOT imply they are behind or failing
            - If a current streak or streak freezes are noted above, you MAY reference that momentum naturally, but never turn it into pressure
            - Describe the ACTIVITY naturally, don't just parrot the exact habit title
            - BAD: "You have Yoga, Morning Routine, and Guitar Playing left."
            - GOOD: "Nice work getting your run in -- some guitar later could be a great way to unwind."
            - Keep it to TWO short sentences -- three only when the day truly needs them -- under ~300 characters total, warm and close, like a friend who actually knows you -- never corporate or coach-like
            - This message is shown for the WHOLE current part of the day, so it must read correctly whether they see it at the start or the end of that window
            - Treat the time of day as a broad window, not an exact moment; never imply a precise instant
            - Do NOT use phrases like "right now", "just woke up", "now that the afternoon is here", "as the day begins", "earlier today", or "upcoming later today"
            - Do NOT use markdown, bullet points, emojis, or JSON
            - Do NOT mention the date explicitly
            - Write ONLY in {languageName}, using natural, fluent, grammatically correct phrasing a native speaker would actually use
            - No greeting like "good morning", no sign-off -- just the message
            """;
    }

    private static string BuildContextHeader(
        TimeOnly? currentLocalTime, int currentStreak, int streakFreezesAccumulated, int badHabitSlips)
    {
        var lines = new List<string> { $"Current part of day: {BuildTimeContext(currentLocalTime)}" };

        if (currentStreak > 0)
            lines.Add($"Current streak: {currentStreak} days");
        if (streakFreezesAccumulated > 0)
            lines.Add($"Streak freezes banked: {streakFreezesAccumulated}");
        if (badHabitSlips > 0)
            lines.Add($"Bad habit slips today: {badHabitSlips}");

        return string.Join("\n", lines);
    }

    private static string BuildHabitSection(
        List<Habit> scheduledHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday)
    {
        var habitLines = new List<string>();

        foreach (var habit in scheduledHabits.Where(h => h.ParentHabitId is null))
        {
            var children = scheduledHabits.Where(h => h.ParentHabitId == habit.Id).ToList();

            if (children.Count > 0)
            {
                var doneCount = children.Count(c => IsDoneInRange(c, dateFrom, dateTo));
                var status = IsDoneInRange(habit, dateFrom, dateTo) ? "done" : "pending";
                habitLines.Add($"- {habit.Title} ({status}, {doneCount}/{children.Count} sub-tasks done) [{DescribeTiming(habit)}]");
                foreach (var child in children)
                    habitLines.Add($"  - {DescribeHabitLine(child, dateFrom, dateTo, userToday)}");
            }
            else
            {
                habitLines.Add($"- {DescribeHabitLine(habit, dateFrom, dateTo, userToday)}");
            }

            AppendGoalsLine(habitLines, habit);
        }

        return habitLines.Count > 0 ? string.Join("\n", habitLines) : "(no habits scheduled)";
    }

    private static string DescribeHabitLine(Habit habit, DateOnly dateFrom, DateOnly dateTo, DateOnly userToday)
    {
        if (habit.IsBadHabit)
            return $"{habit.Title} ({DescribeBadHabitState(habit, dateFrom, dateTo, userToday)}) [{DescribeTiming(habit)}]";

        var status = IsDoneInRange(habit, dateFrom, dateTo)
            ? "done"
            : HabitScheduleService.IsOverdueOnDate(habit, userToday) ? "pending, overdue" : "pending";
        return $"{habit.Title} ({status}) [{DescribeTiming(habit)}]";
    }

    private static string DescribeBadHabitState(Habit habit, DateOnly dateFrom, DateOnly dateTo, DateOnly userToday)
    {
        if (IsDoneInRange(habit, dateFrom, dateTo))
            return "bad habit -- slipped";

        var lastSlip = habit.Logs
            .Where(l => l.Value > 0 && l.Date <= userToday)
            .Select(l => (DateOnly?)l.Date)
            .Max();

        if (lastSlip is null)
            return "bad habit -- clean, no slips on record";

        var daysClean = userToday.DayNumber - lastSlip.Value.DayNumber;
        return $"bad habit -- clean, {daysClean} days since last slip";
    }

    private static void AppendGoalsLine(List<string> habitLines, Habit habit)
    {
        if (habit.Goals.Count == 0)
            return;

        var goalNames = string.Join(", ", habit.Goals.Select(g => g.Title));
        habitLines.Add($"  Goals: {goalNames}");
    }

    private static bool HasSkipLogInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        habit.Logs.Any(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value == 0);

    private static bool IsDoneInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        HabitScheduleService.HasCompletedLogInRange(habit, dateFrom, dateTo);

    /// <summary>
    /// The summary's single inclusion rule: a habit is relevant when it was logged on the viewed
    /// day (completed today), or it is still open — not completed and due on or before the user's
    /// today (due today or overdue). "Done" is decided purely by a dated completion log, never by
    /// the sticky <see cref="Habit.IsCompleted"/> flag, so a task completed on an earlier day (still
    /// flagged completed, but with no log today) is excluded.
    /// </summary>
    private static bool IsRelevant(Habit habit, DateOnly dateFrom, DateOnly dateTo, DateOnly userToday) =>
        IsDoneInRange(habit, dateFrom, dateTo)
        || (!habit.IsCompleted && habit.DueDate <= userToday);

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

    internal static string CapToSentence(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;

        for (var i = maxChars - 1; i >= 0; i--)
        {
            if (text[i] is '.' or '!' or '?')
                return text[..(i + 1)];
        }

        for (var i = maxChars; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return text[..i].TrimEnd();
        }

        return text[..maxChars].TrimEnd();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Generating daily summary (date: {Date}, language: {Language})...")]
    private static partial void LogGeneratingDailySummary(ILogger logger, DateOnly date, string language);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Daily summary generated successfully ({Length} chars)")]
    private static partial void LogDailySummaryGenerated(ILogger logger, int length);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "AI API call failed for daily summary")]
    private static partial void LogDailySummaryFailed(ILogger logger, Exception ex);

}
