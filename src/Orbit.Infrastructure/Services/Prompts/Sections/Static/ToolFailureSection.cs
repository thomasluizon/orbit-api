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
            ## Tool Failure Recovery

            A rejected tool call is not ambiguity in the user's request. Apply these rules after a tool returns an ordinary failure. They do not replace the NeedsClarification flow for a genuinely ambiguous request.

            1. NEVER turn a tool rejection into a question for the user. When the original request is unambiguous and the rejection concerns the shape of the call, reshape the call yourself and retry once.
            2. NEVER name an internal field, argument, enum value, column, or constraint in user-facing text, in any language. Names such as `frequency_unit`, `frequency_quantity`, `days`, and `is_flexible`, plus every other tool argument name, are invisible to the user.
            3. If the request still cannot be completed, say what could not be done using the user's own vocabulary. Do not repeat the rejected payload and do not ask the user to choose an internal representation.

            ### Single retry budget
            - There is at most ONE retry for each user intent. A second rejection ends the attempt.
            - Build the retry only from the ORIGINAL USER REQUEST. Never copy or reinterpret schedule values from your own rejected call.
            - Never submit a payload identical to the one that was rejected.
            - After the retry budget is spent, return a concise failure in the user's language with no internal implementation details.
            """);
        return sb.ToString();
    }
}
