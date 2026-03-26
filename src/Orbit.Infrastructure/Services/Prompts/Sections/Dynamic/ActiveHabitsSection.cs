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

        var parents = context.ActiveHabits.Where(h => h.ParentHabitId is null).ToList();
        var todayHabits = context.UserToday.HasValue
            ? parents.Where(h => !h.IsCompleted && h.DueDate <= context.UserToday.Value).OrderBy(h => h.Position).ToList()
            : [];
        var dueToday = context.UserToday.HasValue
            ? todayHabits.Count(h => h.DueDate == context.UserToday.Value)
            : 0;
        var overdue = todayHabits.Count - dueToday;

        sb.AppendLine($"## User's Habits ({parents.Count} total, {dueToday} due today, {overdue} overdue)");
        sb.AppendLine();
        sb.AppendLine("Use get_todays_schedule to see today's full schedule with details.");
        sb.AppendLine("Use search_habits to find any habit by name.");
        sb.AppendLine("Use get_habits_by_date to check a specific date.");
        sb.AppendLine();

        if (todayHabits.Count > 0)
        {
            sb.AppendLine("### Quick Reference (today + overdue):");
            foreach (var habit in todayHabits)
            {
                var label = habit.DueDate < context.UserToday!.Value ? "OVERDUE" : "TODAY";
                sb.AppendLine($"- \"{habit.Title}\" | {habit.Id} | {label}");

                var children = context.ActiveHabits
                    .Where(h => h.ParentHabitId == habit.Id)
                    .OrderBy(h => h.Position);
                foreach (var child in children)
                    sb.AppendLine($"  - \"{child.Title}\" | {child.Id}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("When user mentions an existing habit -> use the ID from Quick Reference or call search_habits first");
        sb.AppendLine("When user mentions a NEW activity -> use create_habit");

        return sb.ToString();
    }
}
