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
            foreach (var habit in context.ActiveHabits)
            {
                var freqLabel = habit.FrequencyUnit is null
                    ? "One-time"
                    : habit.FrequencyQuantity == 1
                        ? $"Every {habit.FrequencyUnit.ToString()!.ToLower()}"
                        : $"Every {habit.FrequencyQuantity} {habit.FrequencyUnit.ToString()!.ToLower()}s";

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
                sb.AppendLine($"- \"{habit.Title}\" | ID: {habit.Id} | Frequency: {freqLabel} | Due: {habit.DueDate:yyyy-MM-dd}{dueTimeLabel}{badHabitLabel}{slipAlertLabel}{completedLabel}{tagsLabel}{checklistLabel}{metricsLabel}");

                foreach (var child in habit.Children)
                {
                    var childCompleted = child.IsCompleted ? " (done)" : "";
                    sb.AppendLine($"  - \"{child.Title}\" | ID: {child.Id}{childCompleted}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("When user mentions an existing habit activity -> use LogHabit with the exact ID above");
            sb.AppendLine("When user mentions a NEW activity -> use CreateHabit");
        }
        return sb.ToString();
    }
}
