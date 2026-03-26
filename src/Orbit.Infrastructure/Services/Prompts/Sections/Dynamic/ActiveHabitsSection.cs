using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class ActiveHabitsSection : IPromptSection
{
    public int Order => 300;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## User's Active Habits");
        if (context.ActiveHabits.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var habit in context.ActiveHabits.Where(h => h.ParentHabitId is null))
            {
                var freqLabel = habit.FrequencyUnit is null
                    ? "One-time"
                    : habit.IsFlexible
                        ? $"{habit.FrequencyQuantity}x per {habit.FrequencyUnit.ToString()!.ToLower()} (flexible)"
                        : habit.FrequencyQuantity == 1
                            ? $"Every {habit.FrequencyUnit.ToString()!.ToLower()}"
                            : $"Every {habit.FrequencyQuantity} {habit.FrequencyUnit.ToString()!.ToLower()}s";

                var dueLabel = "";
                if (!habit.IsCompleted && context.UserToday.HasValue)
                {
                    if (habit.DueDate == context.UserToday.Value)
                        dueLabel = " | DUE TODAY";
                    else if (habit.DueDate < context.UserToday.Value)
                        dueLabel = " | OVERDUE";
                }

                var badHabitLabel = habit.IsBadHabit ? " | BAD HABIT (tracking to avoid)" : "";
                var slipAlertLabel = habit.SlipAlertEnabled ? " | SLIP ALERTS ON" : "";
                var completedLabel = habit.IsCompleted ? " | COMPLETED" : "";
                var tagsLabel = habit.Tags.Count > 0 ? $" | Tags: [{string.Join(", ", habit.Tags.Select(t => t.Name))}]" : "";

                var metricsLabel = "";
                if (context.HabitMetrics != null && context.HabitMetrics.TryGetValue(habit.Id, out var metrics))
                {
                    var parts = new List<string>();
                    if (metrics.CurrentStreak > 0) parts.Add($"streak: {metrics.CurrentStreak}d");
                    if (metrics.LongestStreak > 0) parts.Add($"best: {metrics.LongestStreak}d");
                    if (metrics.TotalCompletions > 0) parts.Add($"total: {metrics.TotalCompletions}");
                    if (metrics.WeeklyCompletionRate > 0) parts.Add($"week: {metrics.WeeklyCompletionRate:F0}%");
                    if (metrics.LastCompletedDate.HasValue) parts.Add($"last: {metrics.LastCompletedDate.Value:yyyy-MM-dd}");
                    if (parts.Count > 0) metricsLabel = $" | Stats: {string.Join(", ", parts)}";
                }

                var checklistLabel = habit.ChecklistItems.Count > 0
                    ? $" | Checklist: {habit.ChecklistItems.Count(i => i.IsChecked)}/{habit.ChecklistItems.Count} done"
                    : "";

                var dueTimeLabel = habit.DueTime.HasValue ? $" at {habit.DueTime.Value:HH:mm}" : "";
                sb.AppendLine($"- \"{habit.Title}\" | ID: {habit.Id} | Frequency: {freqLabel} | Due: {habit.DueDate:yyyy-MM-dd}{dueTimeLabel}{dueLabel}{badHabitLabel}{slipAlertLabel}{completedLabel}{tagsLabel}{checklistLabel}{metricsLabel}");

                AppendChildren(sb, habit.Id, context, depth: 1);
            }
            sb.AppendLine();
            sb.AppendLine("When user mentions an existing habit activity -> use LogHabit with the exact ID above");
            sb.AppendLine("When user mentions a NEW activity -> use CreateHabit");
        }
        return sb.ToString();
    }

    private static void AppendChildren(StringBuilder sb, Guid parentId, PromptContext context, int depth)
    {
        var children = context.ActiveHabits.Where(h => h.ParentHabitId == parentId);
        var indent = new string(' ', depth * 2);
        foreach (var child in children)
        {
            var childDueLabel = "";
            if (!child.IsCompleted && context.UserToday.HasValue)
            {
                if (child.DueDate == context.UserToday.Value)
                    childDueLabel = " | DUE TODAY";
                else if (child.DueDate < context.UserToday.Value)
                    childDueLabel = " | OVERDUE";
            }
            var childCompleted = child.IsCompleted ? " | COMPLETED" : "";
            sb.AppendLine($"{indent}- [Sub-habit] \"{child.Title}\" | ID: {child.Id} | Due: {child.DueDate:yyyy-MM-dd}{childDueLabel}{childCompleted}");

            AppendChildren(sb, child.Id, context, depth + 1);
        }
    }
}
