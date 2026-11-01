namespace Orbit.Domain.Models;

/// <summary>
/// Every write an agent asks for on the chat surface waits for the person to approve it,
/// whatever risk class or confirmation requirement its capability declares. Other
/// surfaces keep the requirement the catalog declares.
/// </summary>
public static class AgentChatWriteHold
{
    /// <summary>
    /// Reports whether the chat hold escalates <paramref name="effectiveRequirement"/>
    /// to a fresh confirmation for <paramref name="operation"/>.
    /// </summary>
    public static bool Applies(
        AgentExecutionSurface surface,
        AgentOperation operation,
        AgentConfirmationRequirement effectiveRequirement)
    {
        return surface == AgentExecutionSurface.Chat
            && operation.IsMutation
            && effectiveRequirement is not (AgentConfirmationRequirement.FreshConfirmation
                or AgentConfirmationRequirement.StepUp);
    }
}
