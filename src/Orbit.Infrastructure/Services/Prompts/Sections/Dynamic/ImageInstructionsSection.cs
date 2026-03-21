using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class ImageInstructionsSection : IPromptSection
{
    public int Order => 700;
    public bool ShouldInclude(PromptContext context) => context.HasImage;

    public string Build(PromptContext context)
    {
        var today = (context.UserToday ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd");
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            ## Image Analysis Instructions
            When the user uploads an image (photo of schedule, to-do list, calendar, task app screenshot, whiteboard, bill, etc.):

            ### Extraction Rules
            1. Extract EVERYTHING visible: tasks, habits, groups, sub-items, categories, labels
            2. Preserve the EXACT hierarchy from the image:
               - Top-level groups/categories -> separate CreateHabit actions (parents)
               - Nested/indented items under a group -> subHabits array on that parent
               - Deeply nested items (sub-sub-items) -> flatten into the nearest parent's subHabits
            3. Preserve exact titles/names as shown in the image -- do not rename, summarize, or merge items
            4. If an item appears multiple times (e.g., "Water - 710ml" x4), create each one individually
            5. Items with no children that are not nested under anything -> standalone CreateHabit (no subHabits)
            6. Infer frequency from visual cues:
               - Daily checkboxes, daily columns, or checkmarks -> Day, 1
               - Week columns (Mon-Sun) or weekly markers -> Week, 1
               - Month labels -> Month, 1
               - Specific days listed -> use Days array
               - No frequency cue -> default to Day, 1 for recurring items; omit for one-time tasks
            7. Extract due dates from visible dates (YYYY-MM-DD). Default to today if none visible.
            8. Extract times if visible (HH:mm format for dueTime)
            9. Detect completion status: checked/completed items in the image should still be created (user wants the structure, not the state)

            ### When to Create vs Suggest
            - **Create directly** (multiple CreateHabit actions) when user says: "create", "add", "set up", "make these", "I want these", or any clear intent to create
            - **Suggest** (SuggestBreakdown) ONLY when user says: "what do you see?", "analyze this", "what's in this image?" or gives no creation intent at all
            - When in doubt, CREATE. Users send images because they want the habits created.

            ### Example: Task app screenshot with groups (user says "create these habits")
            {
              "aiMessage": "Created 5 habit groups with all their sub-habits from your screenshot!",
              "actions": [
                {
                  "type": "CreateHabit",
                  "title": "Water",
                  "frequencyUnit": "Day",
                  "frequencyQuantity": 1,
                  "dueDate": "{{today}}",
                  "subHabits": [
                    { "title": "Water - 710ml" },
                    { "title": "Water - 710ml" },
                    { "title": "Water - 710ml" },
                    { "title": "Water - 710ml" }
                  ]
                },
                {
                  "type": "CreateHabit",
                  "title": "Meals",
                  "frequencyUnit": "Day",
                  "frequencyQuantity": 1,
                  "dueDate": "{{today}}",
                  "subHabits": [
                    { "title": "Dinner" }
                  ]
                },
                {
                  "type": "CreateHabit",
                  "title": "Waking up",
                  "frequencyUnit": "Day",
                  "frequencyQuantity": 1,
                  "dueDate": "{{today}}"
                },
                {
                  "type": "CreateHabit",
                  "title": "Post Lunch",
                  "frequencyUnit": "Day",
                  "frequencyQuantity": 1,
                  "dueDate": "{{today}}",
                  "subHabits": [
                    { "title": "Creatine" },
                    { "title": "Multivitamin (2)" }
                  ]
                },
                {
                  "type": "CreateHabit",
                  "title": "Anytime",
                  "frequencyUnit": "Day",
                  "frequencyQuantity": 1,
                  "dueDate": "{{today}}",
                  "subHabits": [
                    { "title": "Self care" },
                    { "title": "Exercise" },
                    { "title": "Cardio" }
                  ]
                }
              ]
            }

            """);
        return sb.ToString();
    }
}
