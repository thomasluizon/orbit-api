using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Static;

public class StructuringStrategySection : IPromptSection
{
    public int Order => 250;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            ## Structuring Strategy: Checklist vs Sub-Habits vs Separate Habits

            When the user describes a routine, activity, or task with multiple parts, choose the right structure BEFORE calling create_habit. The wrong structure is one of the most common failure modes.

            ### Use `checklist_items` on a single habit when:
            - Items are atomic sub-steps done together in one execution (not independently tracked)
            - Items are a shopping list, packing list, prep list, or ingredient list
            - Items share the exact same schedule and do not need their own streaks
            - Example: "Weekly supermarket trip to buy bread, sugar, coffee" -> ONE habit "Go to supermarket" with checklist_items=[bread, sugar, coffee]. Do NOT put the items only in the description.
            - Example: "Gym routine with prep: shoes, water bottle, keys" -> parent habit "Go to gym" with checklist_items=[shoes, water bottle, keys]. Do NOT create three weekly sub-habits for the prep steps.

            ### Use `sub_habits` on a parent when:
            - Each sub-activity has its own schedule, time, or independent streak
            - Each sub-activity could reasonably be done at a different time of day
            - Each sub-activity is meaningful on its own (user might ask "did I do X today?")
            - Example: "Morning routine: meditate 10min, journal 5min, 20 pushups" -> parent "Morning routine" with three sub-habits, each trackable independently.
            - Example: "Study routine: prep materials at 7:50 PM, study at 8 PM, review notes at 9:30 PM" -> parent "Study routine" with three time-bound sub-habits OR one parent habit with a checklist of the three steps. Ask the user which they prefer if unclear.

            ### Use multiple separate top-level habits when:
            - Activities are unrelated (no shared context, goal, or grouping)
            - User explicitly lists them as distinct habits
            - They would not make sense grouped under one parent

            ### Ask ONE targeted clarifying question when:
            - User says "weekly" but does not name specific days -> ask which days of the week
            - User says a vague time like "morning" or "evening" without a specific hour
            - Structure is genuinely ambiguous between checklist and sub-habits -> ask "do you want these as a single checklist or individually trackable steps?"
            - Pick the SINGLE most blocking question. NEVER ask more than one at a time.
            - NEVER ask if the user already gave a clear answer elsewhere in the message.
            - After the user answers, act immediately. Do not ask a second round of questions.

            ### Anti-patterns to avoid:
            - Creating 3+ separate daily habits when the user described ONE routine with multiple steps
            - Creating sub-habits for atomic prep steps (use checklist_items instead)
            - Creating a habit that lists items in the description but has no checklist_items
            - Asking multiple clarifying questions when one would unblock you
            - Silently guessing days of the week when the user did not specify them
            """);
        return sb.ToString();
    }
}
