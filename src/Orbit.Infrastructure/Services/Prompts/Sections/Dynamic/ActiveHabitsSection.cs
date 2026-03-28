using System.Text;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class ActiveHabitsSection : IPromptSection
{
    public int Order => 300;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine();

        var parents = context.ActiveHabits.Where(h => h.ParentHabitId is null).ToList();
        var general = parents.Count(h => h.IsGeneral);
        var todayHabits = context.UserToday.HasValue
            ? parents.Where(h => !h.IsCompleted && !h.IsGeneral && h.DueDate <= context.UserToday.Value).OrderBy(h => h.Position).ToList()
            : [];
        var dueToday = context.UserToday.HasValue
            ? todayHabits.Count(h => h.DueDate == context.UserToday.Value)
            : 0;
        var overdue = todayHabits.Count - dueToday;

        sb.AppendLine($"## User's Habits ({parents.Count} total, {general} general, {dueToday} due today, {overdue} overdue)");
        sb.AppendLine();
        sb.AppendLine("Use query_habits to look up any habits. It supports filters: search, date, is_general, is_completed, is_bad_habit, frequency, tag, include_metrics, include_overdue, include_sub_habits, limit.");
        sb.AppendLine("Examples: query_habits(date: 'today'), query_habits(is_general: true), query_habits(tag: 'health'), query_habits(search: 'water')");
        sb.AppendLine();

        if (todayHabits.Count > 0)
        {
            sb.AppendLine("### Quick Reference (today + overdue, excludes general):");
            foreach (var habit in todayHabits)
            {
                var label = habit.DueDate < context.UserToday!.Value ? "OVERDUE" : "TODAY";
                sb.AppendLine($"- \"{SanitizeTitle(habit.Title)}\" | {habit.Id} | {label}");

                if (habit.Goals.Count > 0)
                {
                    var goalNames = string.Join(", ", habit.Goals.Select(g => SanitizeTitle(g.Title)));
                    sb.AppendLine($"  Goals: {goalNames}");
                }

                AppendChildren(sb, context.ActiveHabits, habit.Id, 1);
            }
            sb.AppendLine();
        }

        sb.AppendLine("When user mentions an existing habit -> use the ID from Quick Reference or call query_habits(search: 'name') first");
        sb.AppendLine("When user mentions a NEW activity -> use create_habit");

        return sb.ToString();
    }

    private static void AppendChildren(StringBuilder sb, IReadOnlyList<Habit> allHabits, Guid parentId, int depth)
    {
        var indent = new string(' ', depth * 2);
        var children = allHabits
            .Where(h => h.ParentHabitId == parentId)
            .OrderBy(h => h.Position);
        foreach (var child in children)
        {
            sb.AppendLine($"{indent}- \"{SanitizeTitle(child.Title)}\" | {child.Id}");
            AppendChildren(sb, allHabits, child.Id, depth + 1);
        }
    }

    /// <summary>
    /// Strips control characters (newlines, tabs, etc.) and truncates to 100 chars
    /// to prevent prompt injection via malicious habit titles.
    /// </summary>
    private static string SanitizeTitle(string title)
    {
        // Remove control characters (newlines, tabs, carriage returns, etc.)
        var sanitized = new string(title.Where(c => !char.IsControl(c)).ToArray());
        // Truncate to 100 characters
        return sanitized.Length > 100 ? sanitized[..100] : sanitized;
    }
}
