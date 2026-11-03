namespace Orbit.Application.Chat;

public static class PlanToolDeclaration
{
    public const string Name = "present_plan";
    public const string Instruction = "For two or more action tools, or one write plus approval, call present_plan once BEFORE the action tools in the SAME response. Never call it alone or add a model round for a plan. "
        + "Include the full ordered plan, even for actions in later rounds. Each step names its exact tool and phase. "
        + "Use an execution step for each tool, and a separate approval step after each write's preview preparation. Approval steps describe saving after the person's approval; never claim the write is saved yet. "
        + "For repeated calls of one tool repeat its execution step and approval step in occurrence order. No plan for one read or plain text. "
        + "Write title and steps in the conversation language, at most 40 characters each, one short line. Use plain words; never expose tool identifiers, internal habit types or model names in text. No framing prose around the plan.";

    public static object Declaration => new
    {
        name = Name,
        description = "Publish the turn's plan alongside its first action calls. Does not execute account actions.",
        parameters = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                title = new { type = "string", maxLength = 40 },
                steps = new
                {
                    type = "array",
                    minItems = 2,
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            text = new { type = "string", maxLength = 40 },
                            tool = new { type = "string" },
                            phase = new { type = "string", @enum = new[] { "execution", "approval" } }
                        },
                        required = new[] { "text", "tool", "phase" }
                    }
                }
            },
            required = new[] { "title", "steps" }
        }
    };
}
