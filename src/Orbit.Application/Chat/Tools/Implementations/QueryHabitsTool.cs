using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class QueryHabitsTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository) : IAiTool
{
    public string Name => "query_habits";
    public bool IsReadOnly => true;

    public string Description =>
        "Query and filter the user's habits. Use this tool whenever you need to look up, list, or find habits. Supports filtering by date, search text, frequency, tags, completion status, general habits, bad habits, and more. Always call this before answering questions about habits.";

    public object GetParameterSchema() => new
    {
        type = "object",
        properties = new
        {
            search = new { type = "string", description = "Search habits by title (case-insensitive contains match)" },
            date = new { type = "string", description = "Filter habits due on this date (YYYY-MM-DD). Use 'today' for current date." },
            include_overdue = new { type = "boolean", description = "When filtering by date, also include habits overdue before that date. Default: true when date is 'today', false otherwise." },
            is_general = new { type = "boolean", description = "Filter by general/timeless habits (true) or non-general habits (false). Omit to include both." },
            is_completed = new { type = "boolean", description = "Filter by completion status. Default: only active (false)." },
            is_bad_habit = new { type = "boolean", description = "Filter by bad habit status. Omit to include both." },
            frequency = new { type = "string", description = "Filter by frequency: 'Day', 'Week', 'Month', 'Year', or 'OneTime'.", @enum = new[] { "Day", "Week", "Month", "Year", "OneTime" } },
            tag = new { type = "string", description = "Filter by tag name (case-insensitive)" },
            include_sub_habits = new { type = "boolean", description = "Include sub-habits in results. Default: true" },
            include_metrics = new { type = "boolean", description = "Include streak and completion metrics. Default: false (set true for performance/progress questions)" },
            limit = new { type = "integer", description = "Maximum results to return. Default: 50" }
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

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return new ToolResult(false, Error: "User not found.");

        var today = HabitMetricsCalculator.GetUserToday(user);
        var filters = ParseFilters(args, today);

        var includeMetrics = args.TryGetProperty("include_metrics", out var metricsEl) && metricsEl.ValueKind == JsonValueKind.True;
        var includeSubs = !args.TryGetProperty("include_sub_habits", out var subsEl) || subsEl.ValueKind != JsonValueKind.False;
        var limit = 50;
        if (args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limitEl.GetInt32(), 1, 200);

        var allHabits = await QueryHabitsAsync(userId, filters, includeMetrics, ct);

        var results = allHabits
            .Where(h => h.ParentHabitId is null)
            .OrderBy(h => h.Position)
            .Take(limit)
            .ToList();

        if (results.Count == 0)
            return new ToolResult(true, EntityName: "No habits found matching the given filters.");

        var output = BuildOutput(results, allHabits, today, includeMetrics, includeSubs);
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
        var date = dateStr.Equals("today", StringComparison.OrdinalIgnoreCase)
            ? today
            : DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : today;

        var includeOverdue = dateStr.Equals("today", StringComparison.OrdinalIgnoreCase);
        if (args.TryGetProperty("include_overdue", out var overdueEl))
            includeOverdue = overdueEl.ValueKind == JsonValueKind.True;

        return (date, includeOverdue);
    }

    private async Task<IReadOnlyList<Habit>> QueryHabitsAsync(Guid userId, HabitFilters f, bool includeMetrics, CancellationToken ct)
    {
        return await habitRepository.FindAsync(
            h => h.UserId == userId
                && (f.IsCompleted == null ? !h.IsCompleted : h.IsCompleted == f.IsCompleted.Value)
                && (f.IsGeneral == null || h.IsGeneral == f.IsGeneral.Value)
                && (f.IsBadHabit == null || h.IsBadHabit == f.IsBadHabit.Value)
                && (!f.FrequencyOneTime || h.FrequencyUnit == null)
                && (f.Frequency == null || h.FrequencyUnit == f.Frequency.Value)
                && (!f.Date.HasValue || (!h.IsGeneral && (f.IncludeOverdue ? h.DueDate <= f.Date.Value : h.DueDate == f.Date.Value)))
                && (f.Search == null || h.Title.Contains(f.Search, StringComparison.OrdinalIgnoreCase))
                && (f.Tag == null || h.Tags.Any(t => t.Name.Contains(f.Tag, StringComparison.OrdinalIgnoreCase))),
            includeMetrics
                ? q => q.Include(h => h.Tags).Include(h => h.Logs)
                : q => q.Include(h => h.Tags),
            ct);
    }

    private static string BuildOutput(List<Habit> results, IReadOnlyList<Habit> allHabits, DateOnly today, bool includeMetrics, bool includeSubs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} habit(s):");
        sb.AppendLine();

        foreach (var habit in results)
        {
            sb.AppendLine(BuildHabitLine(habit, today, includeMetrics));

            if (includeSubs)
                AppendChildren(sb, allHabits, habit.Id, today, includeMetrics, 1);
        }

        return sb.ToString();
    }

    private static string BuildHabitLine(Habit habit, DateOnly today, bool includeMetrics)
    {
        var freqLabel = FormatFrequencyLabel(habit);
        var labels = BuildLabels(habit, today, includeMetrics);
        var labelStr = labels.Count > 0 ? $" [{string.Join(" | ", labels)}]" : "";
        return $"- \"{habit.Title}\" | ID: {habit.Id} | {freqLabel} | Due: {habit.DueDate:yyyy-MM-dd}{labelStr}";
    }

    private static List<string> BuildLabels(Habit habit, DateOnly today, bool includeMetrics)
    {
        var labels = new List<string>();
        if (habit.IsGeneral) labels.Add("GENERAL");
        if (!habit.IsGeneral && !habit.IsCompleted && habit.DueDate < today) labels.Add("OVERDUE");
        if (!habit.IsGeneral && !habit.IsCompleted && habit.DueDate == today) labels.Add("DUE TODAY");
        if (habit.IsBadHabit) labels.Add("BAD HABIT");
        if (habit.IsCompleted) labels.Add("COMPLETED");
        if (habit.Tags.Count > 0) labels.Add($"Tags: {string.Join(", ", habit.Tags.Select(t => t.Name))}");

        AddMetricLabels(labels, habit, today, includeMetrics);

        if (habit.ChecklistItems.Count > 0)
            labels.Add($"Checklist: {habit.ChecklistItems.Count(i => i.IsChecked)}/{habit.ChecklistItems.Count}");

        if (includeMetrics && habit.Logs.Any(l => l.Date == today))
            labels.Add("DONE TODAY");

        return labels;
    }

    private static void AddMetricLabels(List<string> labels, Habit habit, DateOnly today, bool includeMetrics)
    {
        if (!includeMetrics) return;
        var metrics = HabitMetricsCalculator.Calculate(habit, today);
        if (metrics.CurrentStreak > 0) labels.Add($"Streak: {metrics.CurrentStreak}d");
        if (metrics.WeeklyCompletionRate > 0) labels.Add($"Week: {metrics.WeeklyCompletionRate:F0}%");
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

    private static void AppendChildren(StringBuilder sb, IReadOnlyList<Habit> allHabits, Guid parentId, DateOnly today, bool includeMetrics, int depth)
    {
        var indent = new string(' ', depth * 2);
        var children = allHabits
            .Where(h => h.ParentHabitId == parentId)
            .OrderBy(h => h.Position);

        foreach (var child in children)
        {
            var childLabels = new List<string>();
            if (includeMetrics && child.Logs.Any(l => l.Date == today)) childLabels.Add("DONE");
            if (child.IsCompleted) childLabels.Add("COMPLETED");
            var childLabelStr = childLabels.Count > 0 ? $" [{string.Join(" | ", childLabels)}]" : "";
            sb.AppendLine($"{indent}- \"{child.Title}\" | ID: {child.Id}{childLabelStr}");
            AppendChildren(sb, allHabits, child.Id, today, includeMetrics, depth + 1);
        }
    }
}
