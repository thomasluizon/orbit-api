namespace Orbit.Domain.Models;

/// <summary>
/// Every write an agent asks for on the chat surface waits for the person to approve it,
/// whatever risk class or confirmation requirement its capability declares. Only the
/// operations in <see cref="ExemptOperationIds"/> stay outside the hold, and other
/// surfaces keep the requirement the catalog declares.
/// </summary>
public static class AgentChatWriteHold
{
    /// <summary>
    /// The closed set of chat write operations that run without a pending operation.
    /// An operation belongs here only when it writes nothing the person can review later.
    /// </summary>
    public static readonly IReadOnlySet<string> ExemptOperationIds =
        new HashSet<string>(StringComparer.Ordinal) { "suggest_breakdown" };

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
            && !ExemptOperationIds.Contains(operation.Id)
            && effectiveRequirement is not (AgentConfirmationRequirement.FreshConfirmation
                or AgentConfirmationRequirement.StepUp);
    }
}
