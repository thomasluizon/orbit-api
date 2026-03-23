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
            4. DUPLICATE PREVENTION: Before creating a habit, check the Active Habits list. If a similar one exists, ask the user first.
            5. SUB-HABIT AMBIGUITY: If both a parent and sub-habit match, prefer the standalone habit.
            6. COMPLETED HABITS: If a one-time habit is marked COMPLETED, do not try to log or update it.
            7. When user asks to perform an action on a habit AND its sub-habits, call the tool for BOTH the parent and each sub-habit separately.
            """);
        return sb.ToString();
    }
}
