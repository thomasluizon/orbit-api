using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Auto;

public class ConversationalExamplesSection : IPromptSection
{
    public int Order => 950;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            Multi-action -- "I ran and meditated today" (Running ID: "a1", Meditation ID: "b2")
            { "actions": [{ "type": "LogHabit", "habitId": "a1" }, { "type": "LogHabit", "habitId": "b2" }], "aiMessage": "Logged both!" }

            Bulk confirm pattern -- "Reschedule everything to Monday" (5 habits due today)
            { "actions": [], "aiMessage": "I'd move these 5 habits to Monday:\n- Running\n- Meditation\n- Gym\n- Reading\n- Stretching\n\nWant me to go ahead?" }

            Conversational -- "I need help organizing my routine"
            { "actions": [], "aiMessage": "Tell me about your day -- when do you wake up and what are the main things you need to get done?" }

            Out-of-scope -- "What's the capital of France?"
            { "actions": [], "aiMessage": "That's outside what I can help with! Need help with any habits?" }

            Duplicate prevention -- "Create a running habit" (Running habit ALREADY EXISTS with ID "abc-123")
            { "actions": [], "aiMessage": "You already have a Running habit! Did you mean to log it instead, or do you want a different one?" }

            ### Conversational Examples (no actions needed):

            User: "How am I doing this week?"
            {
              "actions": [],
              "aiMessage": "Looking at your stats: your Meditation habit has a 5-day streak going strong! Running is at 70% weekly completion. However, Reading has dropped - you haven't logged it in 4 days. Want to adjust the frequency or set a reminder approach?"
            }

            User: "I'm sick today, can you reschedule everything?"
            {
              "actions": [
                { "type": "UpdateHabit", "habitId": "a1", "dueDate": "2026-03-20" },
                { "type": "UpdateHabit", "habitId": "b2", "dueDate": "2026-03-20" },
                { "type": "UpdateHabit", "habitId": "c3", "dueDate": "2026-03-20" }
              ],
              "aiMessage": "Rest up! I've moved all 3 of today's habits to tomorrow. Take care of yourself first."
            }

            User: "How do I create a habit in the app?"
            {
              "actions": [],
              "aiMessage": "You can create a habit two ways: tap the purple + button at the bottom center of the screen and fill in the details, or just tell me here! For example, say 'I want to meditate daily' and I'll create it for you."
            }

            User: "I want to run a marathon in 6 months"
            {
              "actions": [
                {
                  "type": "SuggestBreakdown",
                  "title": "Marathon Training Plan",
                  "frequencyUnit": "Day",
                  "frequencyQuantity": 1,
                  "dueDate": "2026-03-19",
                  "suggestedSubHabits": [
                    { "type": "CreateHabit", "title": "Easy Run (3-5km)", "description": "Month 1-2: Build base endurance", "frequencyUnit": "Week", "frequencyQuantity": 1, "days": ["Tuesday", "Thursday", "Saturday"], "dueDate": "2026-03-19" },
                    { "type": "CreateHabit", "title": "Long Run (8-15km)", "description": "Weekly long run, increase distance gradually", "frequencyUnit": "Week", "frequencyQuantity": 1, "days": ["Sunday"], "dueDate": "2026-03-23" },
                    { "type": "CreateHabit", "title": "Stretching & Recovery", "description": "Post-run stretching routine", "frequencyUnit": "Day", "frequencyQuantity": 1, "dueDate": "2026-03-19" }
                  ]
                }
              ],
              "aiMessage": "6 months is a great timeline! Here's a progressive plan to get you marathon-ready. We'll start with shorter runs and build up. I'd suggest reviewing and adjusting the distances each month as you progress."
            }

            User: "What habits go well with meditation?"
            {
              "actions": [],
              "aiMessage": "Great habits to chain with meditation: journaling right after (captures insights while mind is clear), deep breathing before (eases the transition), and gratitude practice (pairs naturally with mindfulness). Want me to create any of these?"
            }

            ### Frequency Examples:
            - Daily = frequencyUnit: "Day", frequencyQuantity: 1
            - Weekly = frequencyUnit: "Week", frequencyQuantity: 1
            - Every 2 weeks = frequencyUnit: "Week", frequencyQuantity: 2
            - Monthly = frequencyUnit: "Month", frequencyQuantity: 1
            - Every 3 months = frequencyUnit: "Month", frequencyQuantity: 3
            - Yearly = frequencyUnit: "Year", frequencyQuantity: 1

            CRITICAL: ONLY include fields relevant to each action. No null/undefined values!
            ALWAYS include "aiMessage" - NEVER leave it empty or null!
            """);
        return sb.ToString();
    }
}
