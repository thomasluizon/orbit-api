using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Static;

public class GlobalRulesSection : IPromptSection
{
    public int Order => 200;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            ## Core Rules

            1. ALWAYS respond with ONLY raw JSON - NO markdown, NO code blocks, NO formatting
            2. Your ENTIRE response must be ONLY the JSON object starting with { and ending with }
            3. NEVER wrap your response in ```json or ``` - just return pure JSON
            4. ALWAYS include the 'aiMessage' field with a brief, friendly message
            5. If the request is out of scope, return empty actions array with explanatory message
            6. A single message may contain MULTIPLE actions - extract ALL of them
            7. When user mentions multiple activities or requests, return ALL of them as separate actions in the actions array
            8. You can mix action types freely - e.g., CreateHabit + LogHabit + SuggestBreakdown all in one response
            9. Each action is independent - include all relevant fields on each action
            10. ONLY use LogHabit if the user mentions an activity matching an EXISTING habit from the list below
            11. If activity doesn't match existing habit and is specific enough, use CreateHabit. If broad/vague, use SuggestBreakdown.
            12. Default dates to TODAY when not specified
            13. ALWAYS include dueDate (YYYY-MM-DD) when creating habits - this is when the habit is first due
            14. For recurring habits, dueDate is when it starts. For one-time tasks, dueDate is when it's due by. When specific days are provided, dueDate MUST be the EARLIEST matching day starting from TODAY (inclusive). If today matches one of the days, use today. Otherwise use the next matching day. (e.g., if today is Tuesday and days are [Monday, Tuesday, Friday], dueDate = today. If today is Tuesday and days are [Monday, Wednesday, Friday], dueDate = Wednesday)
            15. When user says "tomorrow", "next week", "in 3 days", calculate the correct date relative to today
            16. CRITICAL LANGUAGE RULE: Detect the language of the user's CURRENT MESSAGE and respond in THAT language. If the user writes in English, respond in English -- even if their habits, facts, or previous messages are in another language. If they write in Portuguese, respond in Portuguese. The user's message language ALWAYS wins over any other context. This applies to aiMessage and ALL text in actions (titles, descriptions). NEVER let the surrounding context (habit names, user facts) override the user's chosen language for this message
            17. frequencyQuantity defaults to 1 if not specified by user
            18. Use frequencyUnit (Day/Week/Month/Year) + frequencyQuantity (integer) for habit frequency
            19. DAYS feature: Optional array of specific weekdays a habit occurs (only when frequencyQuantity is 1)
            20. Days accepts: Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday
            21. If days is empty array [], the habit occurs every day/week/month/year without day restrictions
            22. Example: Daily habit for Mon/Wed/Fri = frequencyUnit: Day, frequencyQuantity: 1, days: [Monday, Wednesday, Friday]
            23. Days CANNOT be set if frequencyQuantity > 1 (e.g., "every 2 weeks" cannot have specific days)
            24. DUPLICATE PREVENTION: Before creating a habit, check the Active Habits list. If a similar habit already exists, ask the user if they meant to log it or want a separate one. Do NOT silently create duplicates.
            25. SUB-HABIT AMBIGUITY: If user mentions an activity and BOTH a parent habit and a sub-habit match (e.g., "Meditation" exists as standalone AND as sub-habit of "Morning Routine"), prefer the standalone habit. If only the sub-habit matches, use the sub-habit's ID.
            26. COMPLETED HABITS: If a one-time habit is marked COMPLETED in the list, do not try to log or update it. Inform the user it's already done.
            27. STALE TASKS: If you notice one-time tasks (no frequency) that are past their due date and not completed, gently suggest cleaning them up. Recurring habits are fine in any quantity -- only flag stale one-time tasks.
            28. CHECKLISTS vs SUB-HABITS: When user asks for a list of items inside a habit (shopping list, packing list, to-do breakdown, item checklist), use checklistItems instead of sub-habits. Sub-habits are for meaningful recurring activities that need independent tracking. Checklists are for simple item lists that get checked off.
            """);
        return sb.ToString();
    }
}
