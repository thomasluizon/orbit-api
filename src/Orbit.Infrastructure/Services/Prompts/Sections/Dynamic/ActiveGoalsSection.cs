using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class ActiveGoalsSection : IPromptSection
{
    public int Order => 350;
    public bool ShouldInclude(PromptContext context) => context.ActiveGoals is { Count: > 0 };

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## User's Active Goals");

        foreach (var goal in context.ActiveGoals!)
        {
            var progress = goal.TargetValue > 0
                ? Math.Min(100, Math.Round(goal.CurrentValue / goal.TargetValue * 100, 1))
                : 0;

            var deadlineLabel = goal.Deadline.HasValue ? " | Deadline: " + goal.Deadline.Value.ToString("yyyy-MM-dd") : "";
            var overdueLabel = goal.Deadline.HasValue && context.UserToday.HasValue && goal.Deadline.Value < context.UserToday.Value ? " | OVERDUE" : "";

            sb.AppendLine("- \"" + goal.Title + "\" | ID: " + goal.Id + " | Progress: " + goal.CurrentValue + "/" + goal.TargetValue + " " + goal.Unit + " (" + progress + "%)" + deadlineLabel + overdueLabel);

            if (goal.Habits.Count > 0)
            {
                foreach (var habit in goal.Habits)
                    sb.AppendLine("  - Linked habit: \"" + habit.Title + "\" | " + habit.Id);
            }
        }

        sb.AppendLine();
        sb.AppendLine("When user mentions updating goal progress -> use update_goal_progress with the goal name");
        sb.AppendLine("When user mentions a NEW goal -> use create_goal");
        sb.AppendLine("When user wants to link/associate habits to/with goals -> use link_habits_to_goal");

        return sb.ToString();
    }
}
