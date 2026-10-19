using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public class QueryHabitsTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IHabitScheduleLogReader scheduleLogReader) : IAiTool
{
    public string Name => "query_habits";
    public bool IsReadOnly => true;

    public string Description =>
        "Read current-turn facts for habits: listings, schedules, metrics, streaks, completion percentages, descriptions, checklist items, and filtered searches by text, date, frequency, tag, or status. Call this before answering habit listing or schedule questions; prompt indexes alone do not ground the reply.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            search = new { type = JsonSchemaTypes.String, description = "Search habits by title (case-insensitive contains match)" },
            date = new { type = JsonSchemaTypes.String, description = "Return the canonical schedule for this date (YYYY-MM-DD), including entries already done on that date. Use 'today' for the user's current date. Omit is_completed for the full schedule." },
            include_overdue = new { type = JsonSchemaTypes.Boolean, description = "When filtering by date, also include habits overdue before that date. Default: true when date is 'today', false otherwise." },
            is_general = new { type = JsonSchemaTypes.Boolean, description = "Filter by general/timeless habits (true) or non-general habits (false). Omit to include both." },
            is_completed = new { type = JsonSchemaTypes.Boolean, description = "With date: false returns remaining work, true returns entries done on that date, omitted returns the full schedule. Without date: filter by completion status, default only active (false)." },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean, description = "Filter by bad habit status. Omit to include both." },
            frequency = new { type = JsonSchemaTypes.String, description = "Filter by frequency: 'Day', 'Week', 'Month', 'Year', or 'OneTime'.", @enum = new[] { "Day", "Week", "Month", "Year", "OneTime" } },
            tag = new { type = JsonSchemaTypes.String, description = "Filter by tag name (case-insensitive)" },
            include_sub_habits = new { type = JsonSchemaTypes.Boolean, description = "Include sub-habits in results. Default: true" },
            include_metrics = new { type = JsonSchemaTypes.Boolean, description = "Include streak and completion metrics. Default: false (set true for performance/progress questions)" },
            limit = new { type = JsonSchemaTypes.Integer, description = "Maximum results to return. Default: 50" }
        },
        required = Array.Empty<string>()
    };

    private sealed record HabitFilters(
        bool? IsCompleted,
        bool? IsGeneral,
        bool? IsBadHabit,
        FrequencyUnit? Frequency,
        bool FrequencyOneTime,
        DateOnly? Date,
        bool IncludeOverdue,
        string? Search,
        string? Tag);

    private sealed record ScheduleContext(DateOnly Date, HabitTodaySnapshot Facts);
    private sealed record HabitQueryResult(IReadOnlyList<Habit> Habits, ScheduleContext? Schedule);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return new ToolResult(false, Error: ErrorMessages.UserNotFound.Message);

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var filters = ParseFilters(args, today);

        var includeMetrics = args.TryGetProperty("include_metrics", out var metricsEl) && metricsEl.ValueKind == JsonValueKind.True;
        var includeSubs = !args.TryGetProperty("include_sub_habits", out var subsEl) || subsEl.ValueKind != JsonValueKind.False;
        var limit = 50;
        if (args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limitEl.GetInt32(), 1, 200);

        var query = await QueryHabitsAsync(userId, filters, today, user.WeekStartDay, includeMetrics, ct);
        var allHabits = query.Habits;
        var returnedIds = allHabits.Select(habit => habit.Id).ToHashSet();

        var results = allHabits
            .Where(h => h.ParentHabitId is null
                || query.Schedule is not null && !returnedIds.Contains(h.ParentHabitId.Value))
            .OrderBy(h => h.Position)
            .Take(limit)
            .ToList();

        if (results.Count == 0 && query.Schedule is null)
            return new ToolResult(true, EntityName: "No habits found matching the given filters.");

        var output = BuildOutput(results, allHabits, today, user.WeekStartDay, includeMetrics, includeSubs, query.Schedule);
        return new ToolResult(true, EntityName: output);
    }

    private static HabitFilters ParseFilters(JsonElement args, DateOnly today)
    {
        var isCompleted = ParseBoolFilter(args, "is_completed");
        var isGeneral = ParseBoolFilter(args, "is_general");
        var isBadHabit = ParseBoolFilter(args, "is_bad_habit");

        var (frequency, frequencyOneTime) = ParseFrequencyFilter(args);
        var (dateFilter, includeOverdue) = ParseDateFilter(args, today);

        return new HabitFilters(isCompleted, isGeneral, isBadHabit, frequency, frequencyOneTime,
            dateFilter, includeOverdue, ParseStringFilter(args, "search"), ParseStringFilter(args, "tag"));
    }

    private static bool? ParseBoolFilter(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Null
            ? el.ValueKind == JsonValueKind.True
            : null;

    private static string? ParseStringFilter(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static (FrequencyUnit? Frequency, bool IsOneTime) ParseFrequencyFilter(JsonElement args)
    {
        if (!args.TryGetProperty("frequency", out var freqEl) || freqEl.ValueKind != JsonValueKind.String)
            return (null, false);

        var freqStr = freqEl.GetString() ?? string.Empty;
        if (freqStr.Equals("OneTime", StringComparison.OrdinalIgnoreCase))
            return (null, true);

        return Enum.TryParse<FrequencyUnit>(freqStr, true, out var parsed) ? (parsed, false) : (null, false);
    }

    private static (DateOnly? Date, bool IncludeOverdue) ParseDateFilter(JsonElement args, DateOnly today)
    {
        if (!args.TryGetProperty("date", out var dateEl) || dateEl.ValueKind != JsonValueKind.String)
            return (null, false);

        var dateStr = dateEl.GetString() ?? string.Empty;
        DateOnly date;
        if (dateStr.Equals("today", StringComparison.OrdinalIgnoreCase))
            date = today;
        else if (DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            date = parsed;
        else
            date = today;

        var includeOverdue = dateStr.Equals("today", StringComparison.OrdinalIgnoreCase);
        if (args.TryGetProperty("include_overdue", out var overdueEl))
            includeOverdue = overdueEl.ValueKind == JsonValueKind.True;

        return (date, includeOverdue);
    }

    private async Task<HabitQueryResult> QueryHabitsAsync(
        Guid userId,
        HabitFilters f,
        DateOnly today,
        int weekStartDay,
        bool includeMetrics,
        CancellationToken ct)
    {
        var normalizedSearch = NormalizeSearchValue(f.Search);
        var normalizedTag = NormalizeSearchValue(f.Tag);

        var habits = await habitRepository.FindAsync(
            h => h.UserId == userId
                && (f.IsGeneral == null || h.IsGeneral == f.IsGeneral.Value)
                && (f.IsBadHabit == null || h.IsBadHabit == f.IsBadHabit.Value)
                && (!f.FrequencyOneTime || h.FrequencyUnit == null)
                && (f.Frequency == null || h.FrequencyUnit == f.Frequency.Value)
                && (!f.Date.HasValue || !h.IsGeneral)
                && (normalizedSearch == null || h.Title.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                && (normalizedTag == null || h.Tags.Any(t => t.Name.Contains(normalizedTag, StringComparison.OrdinalIgnoreCase))),
            q => includeMetrics
                ? q.Include(h => h.Tags).Include(h => h.Logs).AsSplitQuery()
                : q
                    .Include(h => h.Tags)
                    .Include(h => h.Logs.Where(l => !l.IsDeleted && l.Date == today && l.Value > 0))
                    .AsSplitQuery(),
            ct);

        if (f.Date is DateOnly date)
        {
            var schedule = await HabitTodaySnapshot.ReadAsync(habits, date, weekStartDay, scheduleLogReader, ct);
            var scheduled = habits.Where(h =>
                (schedule.TodayIds.Contains(h.Id) || f.IncludeOverdue && schedule.OverdueIds.Contains(h.Id))
                && (f.IsCompleted is null || schedule.DoneTodayIds.Contains(h.Id) == f.IsCompleted.Value));
            return new HabitQueryResult(scheduled.ToList(), new ScheduleContext(date, schedule));
        }

        var expectedCompletion = f.IsCompleted ?? false;
        return new HabitQueryResult(habits
            .Where(h => GetResponseCompletion(h, today) == expectedCompletion)
            .ToList(), null);
    }

    private static string? NormalizeSearchValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant();
    }

    private static string BuildOutput(
        List<Habit> results,
        IReadOnlyList<Habit> allHabits,
        DateOnly today,
        int weekStartDay,
        bool includeMetrics,
        bool includeSubs,
        ScheduleContext? schedule)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {results.Count} habit(s):");
        if (schedule is not null)
        {
            var done = results.Count(habit => schedule.Facts.DoneTodayIds.Contains(habit.Id));
            sb.AppendLine(CultureInfo.InvariantCulture, $"Returned schedule for {schedule.Date:yyyy-MM-dd}: {done} done, {results.Count - done} remaining (top-level entries).");
        }
        if (results.Count == 0)
            sb.AppendLine("No habits found matching the given filters.");
        sb.AppendLine();

        var suffixes = SiblingTitleDisambiguator.ComputeSuffixes(results);
        foreach (var habit in results)
        {
            sb.AppendLine(BuildHabitLine(
                habit,
                today,
                weekStartDay,
                includeMetrics,
                suffixes.GetValueOrDefault(habit.Id, string.Empty),
                schedule));

            if (includeSubs)
                AppendChildren(sb, allHabits, habit.Id, today, includeMetrics, 1, schedule);
        }

        return sb.ToString();
    }

    private static string BuildHabitLine(
        Habit habit,
        DateOnly today,
        int weekStartDay,
        bool includeMetrics,
        string dupSuffix,
        ScheduleContext? schedule)
    {
        var freqLabel = FormatFrequencyLabel(habit);
        var labels = BuildLabels(habit, today, weekStartDay, includeMetrics, schedule);
        var labelStr = labels.Count > 0 ? $" [{string.Join(" | ", labels)}]" : "";
        var emojiLabel = string.IsNullOrWhiteSpace(habit.Emoji) ? "No emoji" : $"Emoji: {habit.Emoji}";
        return string.Create(CultureInfo.InvariantCulture, $"- \"{habit.Title}\"{dupSuffix} | ID: {habit.Id} | {emojiLabel} | {freqLabel} | Due: {habit.DueDate:yyyy-MM-dd}{labelStr}");
    }

    private static List<string> BuildLabels(Habit habit, DateOnly today, int weekStartDay, bool includeMetrics, ScheduleContext? schedule)
    {
        var labels = new List<string>();
        var isCompleted = GetResponseCompletion(habit, today);
        if (habit.IsGeneral) labels.Add("GENERAL");
        if (schedule is not null)
        {
            if (schedule.Facts.TodayIds.Contains(habit.Id))
                labels.Add(schedule.Date == today ? "DUE TODAY" : "SCHEDULED ON DATE");
            if (schedule.Facts.OverdueIds.Contains(habit.Id)) labels.Add("OVERDUE");
            if (schedule.Facts.DoneTodayIds.Contains(habit.Id))
                labels.Add(schedule.Date == today ? "DONE TODAY" : "DONE ON DATE");
        }
        else
        {
            if (!habit.IsGeneral && !isCompleted && habit.DueDate < today) labels.Add("OVERDUE");
            if (!habit.IsGeneral && !isCompleted && habit.DueDate == today) labels.Add("DUE TODAY");
        }
        if (habit.IsBadHabit) labels.Add("BAD HABIT");
        if (isCompleted) labels.Add("COMPLETED");
        if (habit.Tags.Count > 0) labels.Add($"Tags: {string.Join(", ", habit.Tags.Select(t => t.Name))}");

        AddMetricLabels(labels, habit, today, weekStartDay, includeMetrics);

        if (habit.ChecklistItems.Count > 0)
            labels.Add($"Checklist: {habit.ChecklistItems.Count(i => i.IsChecked)}/{habit.ChecklistItems.Count}");

        if (schedule is null && includeMetrics && habit.Logs.Any(l => l.Date == today))
            labels.Add("DONE TODAY");

        return labels;
    }

    private static bool GetResponseCompletion(Habit habit, DateOnly today) =>
        habit.IsGeneral
            ? HabitScheduleService.HasCompletedLogInRange(habit, today, today)
            : habit.IsCompleted;

    private static void AddMetricLabels(
        List<string> labels,
        Habit habit,
        DateOnly today,
        int weekStartDay,
        bool includeMetrics)
    {
        if (!includeMetrics) return;
        var metrics = HabitMetricsCalculator.Calculate(habit, today, weekStartDay);
        if (metrics.CurrentStreak > 0) labels.Add($"Streak: {metrics.CurrentStreak}d");
        if (metrics.WeeklyCompletionRate > 0) labels.Add(string.Create(CultureInfo.InvariantCulture, $"Week: {metrics.WeeklyCompletionRate:F0}%"));
        if (metrics.TotalCompletions > 0) labels.Add($"Total: {metrics.TotalCompletions}");
    }

    private static string FormatFrequencyLabel(Habit habit)
    {
        if (habit.FrequencyUnit is null)
            return "One-time";

        var unitName = habit.FrequencyUnit.ToString()!.ToLower();
        return habit.FrequencyQuantity == 1
            ? $"Every {unitName}"
            : $"Every {habit.FrequencyQuantity} {unitName}s";
    }

    private static void AppendChildren(StringBuilder sb, IReadOnlyList<Habit> allHabits, Guid parentId, DateOnly today, bool includeMetrics, int depth, ScheduleContext? schedule)
    {
        var indent = new string(' ', depth * 2);
        var children = allHabits
            .Where(h => h.ParentHabitId == parentId)
            .OrderBy(h => h.Position)
            .ToList();
        var childSuffixes = SiblingTitleDisambiguator.ComputeSuffixes(children);

        foreach (var child in children)
        {
            var childLabels = new List<string>();
            if (schedule is not null && schedule.Facts.DoneTodayIds.Contains(child.Id))
                childLabels.Add(schedule.Date == today ? "DONE TODAY" : "DONE ON DATE");
            else if (schedule is null && includeMetrics && child.Logs.Any(l => l.Date == today)) childLabels.Add("DONE");
            if (GetResponseCompletion(child, today)) childLabels.Add("COMPLETED");
            var childLabelStr = childLabels.Count > 0 ? $" [{string.Join(" | ", childLabels)}]" : "";
            var emojiLabel = string.IsNullOrWhiteSpace(child.Emoji) ? "No emoji" : $"Emoji: {child.Emoji}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}- \"{child.Title}\"{childSuffixes.GetValueOrDefault(child.Id, string.Empty)} | ID: {child.Id} | {emojiLabel}{childLabelStr}");
            AppendChildren(sb, allHabits, child.Id, today, includeMetrics, depth + 1, schedule);
        }
    }
}
