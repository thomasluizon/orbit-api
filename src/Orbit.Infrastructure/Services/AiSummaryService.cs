using System.Globalization;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services.Prompts;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiSummaryService(
    BudgetedAiCompletionClient aiClient,
    ILogger<AiSummaryService> logger) : ISummaryService
{
    private const int MaxSummaryChars = 300;

    internal const string SystemPrompt =
        "You are Astra, a perceptive, warm close friend who knows the person well. You notice and celebrate the good things they have already done, you treat a slip on a habit they are trying to quit as a gentle, judgment-free moment rather than something to praise, and you stay easy and unpushy about what is left. You never sound corporate, clinical, or like a coach reading a checklist. You reply with a single JSON object as instructed. The wording inside every field is plain language, with no markdown, no bullet, no heading and no emoji, no greeting and no sign-off, only in the language you are told to use.";

    public async Task<Result<DailySummaryContent>> GenerateSummaryAsync(
        IEnumerable<Habit> allHabits,
        DailySummaryContext context,
        CancellationToken cancellationToken = default)
    {
        var scheduledHabits = SelectScheduledHabits(
            allHabits,
            context.UserToday,
            context.DateFrom,
            context.DateTo,
            context.WeekStartDay,
            context.ResolvedDueDateHabitIds);

        var prompt = BuildSummaryPrompt(scheduledHabits, context);

        if (logger.IsEnabled(LogLevel.Debug))
            LogGeneratingDailySummary(logger, context.DateFrom, context.Language);

        try
        {
            var content = await aiClient.CompleteJsonAsync<DailySummaryJson>(
                SystemPrompt,
                prompt,
                temperature: 0.3,
                cancellationToken: cancellationToken,
                maxOutputTokens: 8192,
                purpose: "daily_summary",
                userId: context.UserId);

            if (content is null || string.IsNullOrWhiteSpace(content.Summary))
                return Result.Failure<DailySummaryContent>(ErrorMessages.AiEmptyResponse);

            var summary = CapToSentence(StripMarkdownFences(content.Summary), MaxSummaryChars);

            if (logger.IsEnabled(LogLevel.Debug))
                LogDailySummaryGenerated(logger, summary.Length);
            return Result.Success(new DailySummaryContent(summary, string.Empty));
        }
        catch (AiBudgetExceededException)
        {
            return Result.Failure<DailySummaryContent>(AiBudgetExceededException.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDailySummaryFailed(logger, ex);
            return Result.Failure<DailySummaryContent>(ErrorMessages.AiSummaryUnavailable);
        }
    }

    private sealed record DailySummaryJson(string? Summary);

    private static List<Habit> SelectScheduledHabits(
        IEnumerable<Habit> allHabits,
        DateOnly userToday,
        DateOnly dateFrom,
        DateOnly dateTo,
        int weekStartDay,
        IReadOnlySet<Guid>? resolvedDueDateHabitIds)
    {
        var habitList = allHabits.ToList();

        var scheduledTopLevel = habitList
            .Where(h => h.ParentHabitId is null
                          && IsRelevant(
                              h,
                              dateFrom,
                              dateTo,
                              userToday,
                              weekStartDay,
                              resolvedDueDateHabitIds))
            .ToList();

        var scheduledTopLevelIds = scheduledTopLevel.Select(h => h.Id).ToHashSet();

        var children = habitList
            .Where(h => h.ParentHabitId is not null
                        && scheduledTopLevelIds.Contains(h.ParentHabitId.Value)
                        && IsRelevant(
                            h,
                            dateFrom,
                            dateTo,
                            userToday,
                            weekStartDay,
                            resolvedDueDateHabitIds))
            .ToList();

        return scheduledTopLevel.Concat(children).ToList();
    }

    internal static string BuildSummaryPrompt(
        List<Habit> scheduledHabits,
        DailySummaryContext context)
    {
        var languageName = LocaleHelper.GetAiLanguageName(context.Language);

        var habitSection = BuildHabitSection(
            scheduledHabits,
            context.DateFrom,
            context.DateTo,
            context.UserToday,
            context.LastBadHabitSlipDates,
            context.WeekStartDay,
            context.ResolvedDueDateHabitIds);

        var goodHabits = scheduledHabits.Where(h => h.ParentHabitId is null && !h.IsBadHabit).ToList();
        var doneTotal = goodHabits.Count(h => IsDoneInRange(h, context.DateFrom, context.DateTo));
        var badHabitSlips = scheduledHabits.Count(h => h.IsBadHabit && IsDoneInRange(h, context.DateFrom, context.DateTo));
        var contextHeader = BuildContextHeader(
            context.CurrentLocalTime, context.CurrentStreak, context.StreakFreezesAccumulated, badHabitSlips);

        return string.Create(CultureInfo.InvariantCulture, $"""
            Date: {context.DateFrom:MMMM d, yyyy}
            {contextHeader}
            Progress: {doneTotal}/{goodHabits.Count} habits completed

            Today's habits:
            {habitSection}

            Write a short message to this person about their day.

            Rules:
            - LEAD with a specific, genuine acknowledgment of what they have ALREADY completed today. Name the activity naturally, and never settle for "good job"
            - If a habit is marked pending, gently point at at most ONE as an easy next move. Only nudge pending habits in the supplied list; never suggest an unlisted or skipped habit
            - Lines marked "overdue" are the most worth a gentle nudge, but raise at most one and never with guilt
            - A line tagged "bad habit, slipped" is a slip on something they are trying to QUIT: never congratulate it, never count it as a win; acknowledge it briefly and kindly, or simply focus elsewhere
            - A line tagged "bad habit, clean" means they have stayed away from something they are trying to quit: THAT is the real win worth naming warmly; for these, fewer slips and longer clean streaks are the progress
            - If nothing is pending, do NOT invent, imply, or suggest any remaining task. Celebrate only recorded completions; if no habits are scheduled or all were skipped, stay warm without claiming a full completed day
            - A habit marked skipped is not completed and is not pending. Never praise a skip or nudge it again
            - If nothing is done yet, stay warm and forward-looking. Do NOT imply they are behind or failing
            - Never attribute the app-wide streak to a single habit. It measures active days across the app, not consecutive completions of any one habit; omit it or explicitly call it app-wide
            - Consistency claims require at least two completed scheduled occurrences in that habit's multi-day history. Never extend a claim beyond the supplied window. Never praise consistency when skipped or missed is nonzero, or history is unavailable. A habit streak counts scheduled occurrences within this history window, not calendar days or a lifetime record
            - History ends on the earlier of the requested end date and the user's today. Today's unresolved occurrence is pending, never missed. Flexible quotas have no inferred daily misses or streak; do not praise their consistency
            - You may reference banked streak freezes without turning them into pressure
            - Describe the ACTIVITY naturally, don't just parrot the exact habit title
            - BAD: "You have Yoga, Morning Routine, and Guitar Playing left."
            - GOOD: "Nice work getting your run in. Some guitar later could be an easy one to land."
            - Keep it to TWO short sentences, three only when the day truly needs them, under ~300 characters total, warm and close, like a friend who actually knows you, never corporate or coach-like
            - This message is shown for the WHOLE current part of the day, so it must read correctly whether they see it at the start or the end of that window
            - Treat the time of day as a broad window, not an exact moment; never imply a precise instant
            - Do NOT use phrases like "right now", "just woke up", "now that the afternoon is here", "as the day begins", "earlier today", or "upcoming later today"
            - Do NOT use markdown, bullet points, or emojis in the wording
            - Do NOT mention the date explicitly
            - Write ONLY in {languageName}, using natural, fluent, grammatically correct phrasing a native speaker would actually use
            - No greeting like "good morning", no sign-off, just the message

            {NotificationVoice.Rules}

            Respond with ONLY a JSON object with one string field and nothing else: "summary" (the message above).
            """);
    }

    private static string BuildContextHeader(
        TimeOnly? currentLocalTime, int currentStreak, int streakFreezesAccumulated, int badHabitSlips)
    {
        var lines = new List<string> { $"Current part of day: {BuildTimeContext(currentLocalTime)}" };

        if (currentStreak > 0)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"App-wide active-day streak: {currentStreak} days"));
        if (streakFreezesAccumulated > 0)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Streak freezes banked: {streakFreezesAccumulated}"));
        if (badHabitSlips > 0)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Bad habit slips today: {badHabitSlips}"));

        return string.Join("\n", lines);
    }

    private static string BuildHabitSection(
        List<Habit> scheduledHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday,
        IReadOnlyDictionary<Guid, DateOnly> lastBadHabitSlipDates,
        int weekStartDay,
        IReadOnlySet<Guid>? resolvedDueDateHabitIds)
    {
        var habitLines = new List<string>();

        foreach (var habit in scheduledHabits.Where(h => h.ParentHabitId is null))
        {
            var children = scheduledHabits.Where(h => h.ParentHabitId == habit.Id).ToList();

            if (children.Count > 0)
            {
                var doneCount = children.Count(c => IsDoneInRange(c, dateFrom, dateTo));
                var subTasks = string.Create(CultureInfo.InvariantCulture, $", {doneCount}/{children.Count} sub-tasks done");
                habitLines.Add($"- {DescribeHabitLine(habit, dateFrom, dateTo, userToday, lastBadHabitSlipDates, weekStartDay, resolvedDueDateHabitIds, subTasks)}");
                foreach (var child in children)
                    habitLines.Add($"  - {DescribeHabitLine(child, dateFrom, dateTo, userToday, lastBadHabitSlipDates, weekStartDay, resolvedDueDateHabitIds)}");
            }
            else
            {
                habitLines.Add($"- {DescribeHabitLine(habit, dateFrom, dateTo, userToday, lastBadHabitSlipDates, weekStartDay, resolvedDueDateHabitIds)}");
            }

            AppendGoalsLine(habitLines, habit);
        }

        return habitLines.Count > 0 ? string.Join("\n", habitLines) : "(no habits scheduled)";
    }

    private static string DescribeHabitLine(
        Habit habit, DateOnly dateFrom, DateOnly dateTo, DateOnly userToday,
        IReadOnlyDictionary<Guid, DateOnly> lastBadHabitSlipDates,
        int weekStartDay,
        IReadOnlySet<Guid>? resolvedDueDateHabitIds,
        string subTasks = "")
    {
        if (habit.IsBadHabit)
            return $"{habit.Title} ({DescribeBadHabitState(habit, dateFrom, dateTo, userToday, lastBadHabitSlipDates)}{subTasks}) [{DescribeTiming(habit)}] {DescribeHistory(habit, dateTo, userToday, weekStartDay)}";

        string status;
        if (HasSkipLogInRange(habit, dateFrom, dateTo))
            status = "skipped";
        else if (IsDoneInRange(habit, dateFrom, dateTo))
            status = "done";
        else
            status = HabitScheduleService.IsOverdueOnDate(
                habit,
                userToday,
                weekStartDay,
                resolvedDueDateHabitIds?.Contains(habit.Id) == true)
                ? "pending, overdue"
                : "pending";
        return $"{habit.Title} ({status}{subTasks}) [{DescribeTiming(habit)}] {DescribeHistory(habit, dateTo, userToday, weekStartDay)}";
    }

    private static string DescribeHistory(Habit habit, DateOnly dateTo, DateOnly userToday, int weekStartDay)
    {
        var to = dateTo < userToday ? dateTo : userToday;
        var from = to.AddDays(-Math.Min(AppConstants.DailySummaryHistoryDays - 1, to.DayNumber));
        var logs = habit.Logs.Where(log => !log.IsDeleted && log.Date >= from && log.Date <= to).ToList();
        var doneDates = logs.Where(log => log.Value > 0).Select(log => log.Date).ToHashSet();
        var skipDates = logs.Where(log => log.Value == 0).Select(log => log.Date).ToHashSet();
        if (habit.IsBadHabit)
            return string.Create(CultureInfo.InvariantCulture,
                $"[{AppConstants.DailySummaryHistoryDays}-day history: slips={logs.Count(log => log.Value > 0)}, skipped={skipDates.Count}]");
        if (habit.Schedule == HabitSchedule.Flexible)
            return string.Create(CultureInfo.InvariantCulture,
                $"[{AppConstants.DailySummaryHistoryDays}-day history: done={logs.Count(log => log.Value > 0)}, skipped={logs.Count(log => log.Value == 0)}, missed=unknown, habit streak=unknown (flexible quota)]");

        var missed = 0;
        var streak = 0;
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (!HabitScheduleService.WasScheduledOnDate(habit, date, weekStartDay))
                continue;
            if (skipDates.Contains(date))
                streak = 0;
            else if (doneDates.Contains(date))
                streak++;
            else if (date < userToday)
            {
                missed++;
                streak = 0;
            }
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"[{AppConstants.DailySummaryHistoryDays}-day history: done={doneDates.Count}, skipped={skipDates.Count}, missed={missed}, habit streak={streak} scheduled occurrences]");
    }

    private static string DescribeBadHabitState(
        Habit habit, DateOnly dateFrom, DateOnly dateTo, DateOnly userToday,
        IReadOnlyDictionary<Guid, DateOnly> lastBadHabitSlipDates)
    {
        if (IsDoneInRange(habit, dateFrom, dateTo))
            return "bad habit, slipped";

        if (!lastBadHabitSlipDates.TryGetValue(habit.Id, out var lastSlip))
            return "bad habit, clean, no slips on record";

        var daysClean = userToday.DayNumber - lastSlip.DayNumber;
        return string.Create(CultureInfo.InvariantCulture, $"bad habit, clean, {daysClean} days since last slip");
    }

    private static void AppendGoalsLine(List<string> habitLines, Habit habit)
    {
        if (habit.Goals.Count == 0)
            return;

        var goalNames = string.Join(", ", habit.Goals.Select(g => g.Title));
        habitLines.Add($"  Goals: {goalNames}");
    }

    private static bool HasSkipLogInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        habit.Logs.Any(l => !l.IsDeleted && l.Date >= dateFrom && l.Date <= dateTo && l.Value == 0);

    private static bool IsDoneInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        HabitScheduleService.HasCompletedLogInRange(habit, dateFrom, dateTo);

    private static bool IsRelevant(
        Habit habit,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday,
        int weekStartDay,
        IReadOnlySet<Guid>? resolvedDueDateHabitIds) =>
        IsDoneInRange(habit, dateFrom, dateTo)
        || HasSkipLogInRange(habit, dateFrom, dateTo)
        || (!habit.IsCompleted
            && (HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo, weekStartDay)
                .Any(date => habit.Schedule != HabitSchedule.Flexible || HabitScheduleService.IsFlexibleHabitDueOnDate(habit, date, habit.Logs, weekStartDay))
                || HabitScheduleService.IsOverdueOnDate(
                    habit,
                    userToday,
                    weekStartDay,
                    resolvedDueDateHabitIds?.Contains(habit.Id) == true)));

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
            ? string.Create(CultureInfo.InvariantCulture, $"due {habit.DueTime.Value:HH\\:mm}")
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Generating daily summary (date: {Date}, language: {Language})...")]
    private static partial void LogGeneratingDailySummary(ILogger logger, DateOnly date, string language);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Daily summary generated successfully ({Length} chars)")]
    private static partial void LogDailySummaryGenerated(ILogger logger, int length);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "AI API call failed for daily summary")]
    private static partial void LogDailySummaryFailed(ILogger logger, Exception ex);

}
