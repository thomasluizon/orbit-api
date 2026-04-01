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

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return new ToolResult(false, Error: "User not found.");

        var today = HabitMetricsCalculator.GetUserToday(user);

        var includeMetrics = args.TryGetProperty("include_metrics", out var metricsEl) && metricsEl.ValueKind == JsonValueKind.True;
        var includeSubs = !args.TryGetProperty("include_sub_habits", out var subsEl) || subsEl.ValueKind != JsonValueKind.False;
        var limit = 50;
        if (args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limitEl.GetInt32(), 1, 200);

        // Parse filters up-front so we can push them to the DB predicate
        bool? isCompletedFilter = null;
        if (args.TryGetProperty("is_completed", out var completedEl) && completedEl.ValueKind != JsonValueKind.Null)
            isCompletedFilter = completedEl.ValueKind == JsonValueKind.True;

        bool? isGeneralFilter = null;
        if (args.TryGetProperty("is_general", out var generalEl) && generalEl.ValueKind != JsonValueKind.Null)
            isGeneralFilter = generalEl.ValueKind == JsonValueKind.True;

        bool? isBadHabitFilter = null;
        if (args.TryGetProperty("is_bad_habit", out var badEl) && badEl.ValueKind != JsonValueKind.Null)
            isBadHabitFilter = badEl.ValueKind == JsonValueKind.True;

        FrequencyUnit? frequencyFilter = null;
        var frequencyOneTime = false;
        if (args.TryGetProperty("frequency", out var freqEl) && freqEl.ValueKind == JsonValueKind.String)
        {
            var freqStr = freqEl.GetString() ?? string.Empty;
            if (freqStr.Equals("OneTime", StringComparison.OrdinalIgnoreCase))
                frequencyOneTime = true;
            else if (Enum.TryParse<FrequencyUnit>(freqStr, true, out var parsedFreq))
                frequencyFilter = parsedFreq;
        }

        DateOnly? dateFilter = null;
        var includeOverdue = false;
        if (args.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
        {
            var dateStr = dateEl.GetString() ?? string.Empty;
            dateFilter = dateStr.Equals("today", StringComparison.OrdinalIgnoreCase) ? today
                : DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) ? parsedDate : today;
            includeOverdue = dateStr.Equals("today", StringComparison.OrdinalIgnoreCase);
            if (args.TryGetProperty("include_overdue", out var overdueEl))
                includeOverdue = overdueEl.ValueKind == JsonValueKind.True;
        }

        string? searchText = null;
        if (args.TryGetProperty("search", out var searchEl) && searchEl.ValueKind == JsonValueKind.String)
            searchText = searchEl.GetString();

        string? tagFilter = null;
        if (args.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
            tagFilter = tagEl.GetString();

        // Capture loop variables for use in lambda (avoid closure over mutable vars)
        var capturedDate = dateFilter;
        var capturedIncludeOverdue = includeOverdue;
        var capturedSearch = searchText;
        var capturedTag = tagFilter;
        var capturedIsCompleted = isCompletedFilter;
        var capturedIsGeneral = isGeneralFilter;
        var capturedIsBad = isBadHabitFilter;
        var capturedFreqOneTime = frequencyOneTime;
        var capturedFreq = frequencyFilter;

        // Apply DB-level filtering to avoid loading all user habits into memory
        IReadOnlyList<Habit> allHabits;
        if (includeMetrics)
        {
            allHabits = await habitRepository.FindAsync(
                h => h.UserId == userId
                    && (capturedIsCompleted == null ? !h.IsCompleted : h.IsCompleted == capturedIsCompleted.Value)
                    && (capturedIsGeneral == null || h.IsGeneral == capturedIsGeneral.Value)
                    && (capturedIsBad == null || h.IsBadHabit == capturedIsBad.Value)
                    && (!capturedFreqOneTime || h.FrequencyUnit == null)
                    && (capturedFreq == null || h.FrequencyUnit == capturedFreq.Value)
                    && (!capturedDate.HasValue || (!h.IsGeneral && (capturedIncludeOverdue ? h.DueDate <= capturedDate.Value : h.DueDate == capturedDate.Value)))
                    && (capturedSearch == null || h.Title.ToLower().Contains(capturedSearch.ToLower()))
                    && (capturedTag == null || h.Tags.Any(t => t.Name.ToLower().Contains(capturedTag.ToLower()))),
                q => q.Include(h => h.Tags).Include(h => h.Logs),
                ct);
        }
        else
        {
            allHabits = await habitRepository.FindAsync(
                h => h.UserId == userId
                    && (capturedIsCompleted == null ? !h.IsCompleted : h.IsCompleted == capturedIsCompleted.Value)
                    && (capturedIsGeneral == null || h.IsGeneral == capturedIsGeneral.Value)
                    && (capturedIsBad == null || h.IsBadHabit == capturedIsBad.Value)
                    && (!capturedFreqOneTime || h.FrequencyUnit == null)
                    && (capturedFreq == null || h.FrequencyUnit == capturedFreq.Value)
                    && (!capturedDate.HasValue || (!h.IsGeneral && (capturedIncludeOverdue ? h.DueDate <= capturedDate.Value : h.DueDate == capturedDate.Value)))
                    && (capturedSearch == null || h.Title.ToLower().Contains(capturedSearch.ToLower()))
                    && (capturedTag == null || h.Tags.Any(t => t.Name.ToLower().Contains(capturedTag.ToLower()))),
                q => q.Include(h => h.Tags),
                ct);
        }

        // Start with parent habits only
        var results = allHabits
            .Where(h => h.ParentHabitId is null)
            .OrderBy(h => h.Position)
            .Take(limit)
            .ToList();

        if (results.Count == 0)
            return new ToolResult(true, EntityName: "No habits found matching the given filters.");

        // Build output
        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} habit(s):");
        sb.AppendLine();

        foreach (var habit in results)
        {
            var freqLabel = habit.FrequencyUnit is null ? "One-time" :
                habit.FrequencyQuantity == 1 ? $"Every {habit.FrequencyUnit.ToString()!.ToLower()}" :
                $"Every {habit.FrequencyQuantity} {habit.FrequencyUnit.ToString()!.ToLower()}s";

            var labels = new List<string>();
            if (habit.IsGeneral) labels.Add("GENERAL");
            if (!habit.IsGeneral && !habit.IsCompleted && habit.DueDate < today) labels.Add("OVERDUE");
            if (!habit.IsGeneral && !habit.IsCompleted && habit.DueDate == today) labels.Add("DUE TODAY");
            if (habit.IsBadHabit) labels.Add("BAD HABIT");
            if (habit.IsCompleted) labels.Add("COMPLETED");
            if (habit.Tags.Count > 0) labels.Add($"Tags: {string.Join(", ", habit.Tags.Select(t => t.Name))}");

            if (includeMetrics)
            {
                var metrics = HabitMetricsCalculator.Calculate(habit, today);
                if (metrics.CurrentStreak > 0) labels.Add($"Streak: {metrics.CurrentStreak}d");
                if (metrics.WeeklyCompletionRate > 0) labels.Add($"Week: {metrics.WeeklyCompletionRate:F0}%");
                if (metrics.TotalCompletions > 0) labels.Add($"Total: {metrics.TotalCompletions}");
            }

            if (habit.ChecklistItems.Count > 0)
            {
                var done = habit.ChecklistItems.Count(i => i.IsChecked);
                labels.Add($"Checklist: {done}/{habit.ChecklistItems.Count}");
            }

            var loggedToday = includeMetrics && habit.Logs.Any(l => l.Date == today);
            if (loggedToday) labels.Add("DONE TODAY");

            var labelStr = labels.Count > 0 ? $" [{string.Join(" | ", labels)}]" : "";
            sb.AppendLine($"- \"{habit.Title}\" | ID: {habit.Id} | {freqLabel} | Due: {habit.DueDate:yyyy-MM-dd}{labelStr}");

            // Sub-habits (recursive, all depth levels)
            if (includeSubs)
            {
                AppendChildren(sb, allHabits, habit.Id, today, includeMetrics, 1);
            }
        }

        return new ToolResult(true, EntityName: sb.ToString());
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
