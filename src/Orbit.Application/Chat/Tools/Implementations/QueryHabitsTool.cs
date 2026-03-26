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
        type = "OBJECT",
        properties = new
        {
            search = new { type = "STRING", description = "Search habits by title (case-insensitive contains match)" },
            date = new { type = "STRING", description = "Filter habits due on this date (YYYY-MM-DD). Use 'today' for current date." },
            include_overdue = new { type = "BOOLEAN", description = "When filtering by date, also include habits overdue before that date. Default: true when date is 'today', false otherwise." },
            is_general = new { type = "BOOLEAN", description = "Filter by general/timeless habits (true) or non-general habits (false). Omit to include both." },
            is_completed = new { type = "BOOLEAN", description = "Filter by completion status. Default: only active (false)." },
            is_bad_habit = new { type = "BOOLEAN", description = "Filter by bad habit status. Omit to include both." },
            frequency = new { type = "STRING", description = "Filter by frequency: 'Day', 'Week', 'Month', 'Year', or 'OneTime'.", @enum = new[] { "Day", "Week", "Month", "Year", "OneTime" } },
            tag = new { type = "STRING", description = "Filter by tag name (case-insensitive)" },
            include_sub_habits = new { type = "BOOLEAN", description = "Include sub-habits in results. Default: true" },
            include_metrics = new { type = "BOOLEAN", description = "Include streak and completion metrics. Default: false (set true for performance/progress questions)" },
            limit = new { type = "INTEGER", description = "Maximum results to return. Default: 50" }
        },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return new ToolResult(false, Error: "User not found.");

        var today = HabitMetricsCalculator.GetUserToday(user);

        // Determine if we need logs (for metrics or completion status)
        var includeMetrics = args.TryGetProperty("include_metrics", out var metricsEl) && metricsEl.ValueKind == JsonValueKind.True;
        var includeSubs = !args.TryGetProperty("include_sub_habits", out var subsEl) || subsEl.ValueKind != JsonValueKind.False;
        var limit = 50;
        if (args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limitEl.GetInt32(), 1, 200);

        // Load habits with appropriate includes
        IReadOnlyList<Habit> allHabits;
        if (includeMetrics)
        {
            allHabits = await habitRepository.FindAsync(
                h => h.UserId == userId,
                q => q.Include(h => h.Tags).Include(h => h.Logs),
                ct);
        }
        else
        {
            allHabits = await habitRepository.FindAsync(
                h => h.UserId == userId,
                q => q.Include(h => h.Tags),
                ct);
        }

        // Start with parent habits only
        var query = allHabits.Where(h => h.ParentHabitId is null).AsEnumerable();

        // Apply filters
        if (args.TryGetProperty("search", out var searchEl) && searchEl.ValueKind == JsonValueKind.String)
        {
            var searchText = searchEl.GetString()!;
            // Search all habits (any depth), then walk up to root parent for display
            var matchingRootIds = allHabits
                .Where(h => h.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                .Select(h => GetRootParentId(allHabits, h))
                .ToHashSet();
            query = query.Where(h => matchingRootIds.Contains(h.Id));
        }

        // Date filter
        if (args.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
        {
            var dateStr = dateEl.GetString()!;
            var targetDate = dateStr.Equals("today", StringComparison.OrdinalIgnoreCase) ? today : DateOnly.Parse(dateStr);

            var includeOverdue = dateStr.Equals("today", StringComparison.OrdinalIgnoreCase);
            if (args.TryGetProperty("include_overdue", out var overdueEl))
                includeOverdue = overdueEl.ValueKind == JsonValueKind.True;

            query = query.Where(h => !h.IsGeneral && (includeOverdue ? h.DueDate <= targetDate : h.DueDate == targetDate));
        }

        // General filter
        if (args.TryGetProperty("is_general", out var generalEl) && generalEl.ValueKind != JsonValueKind.Null)
        {
            var isGeneral = generalEl.ValueKind == JsonValueKind.True;
            query = query.Where(h => h.IsGeneral == isGeneral);
        }

        // Completion filter (default: active only)
        if (args.TryGetProperty("is_completed", out var completedEl) && completedEl.ValueKind != JsonValueKind.Null)
        {
            var isCompleted = completedEl.ValueKind == JsonValueKind.True;
            query = query.Where(h => h.IsCompleted == isCompleted);
        }
        else
        {
            query = query.Where(h => !h.IsCompleted);
        }

        // Bad habit filter
        if (args.TryGetProperty("is_bad_habit", out var badEl) && badEl.ValueKind != JsonValueKind.Null)
        {
            var isBad = badEl.ValueKind == JsonValueKind.True;
            query = query.Where(h => h.IsBadHabit == isBad);
        }

        // Frequency filter
        if (args.TryGetProperty("frequency", out var freqEl) && freqEl.ValueKind == JsonValueKind.String)
        {
            var freqStr = freqEl.GetString()!;
            if (freqStr.Equals("OneTime", StringComparison.OrdinalIgnoreCase))
                query = query.Where(h => h.FrequencyUnit is null);
            else if (Enum.TryParse<FrequencyUnit>(freqStr, true, out var unit))
                query = query.Where(h => h.FrequencyUnit == unit);
        }

        // Tag filter
        if (args.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
        {
            var tagName = tagEl.GetString()!;
            query = query.Where(h => h.Tags.Any(t => t.Name.Contains(tagName, StringComparison.OrdinalIgnoreCase)));
        }

        var results = query.OrderBy(h => h.Position).Take(limit).ToList();

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

    private static Guid GetRootParentId(IReadOnlyList<Habit> allHabits, Habit habit)
    {
        var current = habit;
        for (var i = 0; i < 10 && current.ParentHabitId is not null; i++)
        {
            var parent = allHabits.FirstOrDefault(h => h.Id == current.ParentHabitId);
            if (parent is null) break;
            current = parent;
        }
        return current.Id;
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
