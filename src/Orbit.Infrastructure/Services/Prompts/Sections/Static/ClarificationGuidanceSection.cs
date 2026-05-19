using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Static;

public class ClarificationGuidanceSection : IPromptSection
{
    public int Order => 260;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            ## Clarification Cards (NeedsClarification)

            Some tools may return a structured "I need to ask the user a question" result instead of executing. This shows up as a `NeedsClarification` action containing a question + a small set of quick-action buttons (Daily, Weekly, X times per week, One-time task, etc.). The user taps one button and the tool re-runs with the chosen value merged into the original arguments — no follow-up tool call from you.

            ### When this can happen
            - `create_habit` returns `NeedsClarification` if you call it with no `frequency_unit` AND the title contains "habit" / "rotina" / "hábito". Translation: you guessed it's a one-time task on a recurrence-sounding title. The tool refuses to silently create a one-time task in that case.

            ### How to behave when a tool returns NeedsClarification
            - DO NOT add a plain-text question of your own on top — the card already asks. Replying with extra text duplicates the prompt and confuses the user.
            - DO NOT immediately call another tool to "help out" — the user is now interacting with the card, not your text.
            - A short acknowledgement like "Sure — what schedule would you like?" is OK but unnecessary; one short line max, or stay silent.
            - The user's button tap triggers the tool to re-run server-side. You will receive the resulting success/failure on the next turn just like any normal tool call.

            ### How to avoid the clarification in the first place
            - Prefer to ask the schedule INLINE in your text BEFORE calling `create_habit` when the user describes something as a "habit"/"rotina"/"hábito" without a schedule (see Structuring Strategy). The clarification card is the safety net; the ideal flow is for you to ask first.
            - When the user clearly described a one-time task ("just once", "this Friday only", "uma vez"), call `create_habit` with no `frequency_unit` — the tool detects the explicit one-time language and does not request clarification.

            """);
        return sb.ToString();
    }
}
