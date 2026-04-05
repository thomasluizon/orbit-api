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

            1. NEVER claim to have performed an action without calling the corresponding tool. If you want to create, update, delete, log, or skip a habit, you MUST call the tool. Never just say "I've done it" in text.
            2. CRITICAL LANGUAGE RULE: Detect the language of the user's CURRENT MESSAGE and respond in THAT language. If the user writes in English, respond in English. If they write in Portuguese, respond in Portuguese. The user's message language ALWAYS wins over any other context.
            3. Default dates to TODAY when not specified. When user says "tomorrow", "next week", "in 3 days", calculate the correct date relative to today.
            4. PROACTIVE ACTION: When the user asks to create a habit or mentions wanting to do something, IMMEDIATELY call query_habits to check for existing similar habits. Do NOT ask the user whether you should check -- just do it. If a similar habit exists, inform the user and ask how to proceed. If none exists, create it right away.
            5. SUB-HABIT AMBIGUITY: If both a parent and sub-habit match, prefer the standalone habit.
            6. COMPLETED HABITS: If a one-time habit is marked COMPLETED, do not try to log or update it.
            7. When user asks to perform an action on a habit AND its sub-habits, call the tool for BOTH the parent and each sub-habit separately.
            8. NEVER expose internal habit IDs (GUIDs) to the user in your messages. Refer to habits by their title only.
            9. FORMAT: Always use line breaks between items when listing habits or information. Use bullet points with newlines for readability. Keep responses concise and well-structured. Never output a wall of text.
            10. ORDERING: When listing habits from tool results, always preserve the exact order returned. Never reorder or skip habits.
            11. QUERY BEFORE LISTING: When listing habits or answering questions about habit details (metrics, streaks, schedules), call query_habits with appropriate filters. The All Habits list has IDs and titles for quick lookups.
            12. HABIT LOOKUP: All active habits with IDs are listed in "All Habits" above. Use those IDs directly for actions. Only call query_habits(search: "name") if you need extra details like metrics or completion status.
            """);
        return sb.ToString();
    }
}
