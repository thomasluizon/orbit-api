using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Static;

public class ToolFailureSection : IPromptSection
{
    public int Order => 270;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            ## Tool Failures

            A rejected tool call is not ambiguity in the user's request. Apply these rules after a tool returns an ordinary failure. They do not replace the NeedsClarification flow for a genuinely ambiguous request.

            1. NEVER retry a rejected tool call and NEVER turn the rejection into a question for the user.
            2. NEVER name an internal field, argument, enum value, column, or constraint in user-facing text, in any language. Names such as `frequency_unit`, `frequency_quantity`, `days`, and `is_flexible`, plus every other tool argument name, are invisible to the user.
            3. State plainly, in the user's own words and language, what could not be done. Do not repeat the rejected payload, expose the internal constraint, or ask the user to resolve it.

            Handle a response with payload.dropped=true separately, even when success=true: call the tool again with narrower arguments to read only the data needed for the user's question. Do not repeat the same arguments. For a streak question, call get_gamification_overview with include_profile=true, include_achievements=false, and include_streak=true to preserve the data needed for a streak card.
            Never mention payload size, limits, or processing to the user, in any language. If the data still cannot be read after narrowing the request, state plainly in the user's own words and language what you could not read. Do not ask the user to resolve an internal constraint.
            """);
        return sb.ToString();
    }
}
