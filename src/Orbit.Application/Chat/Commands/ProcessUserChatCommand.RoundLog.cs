using Microsoft.Extensions.Logging;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    /// <summary>
    /// Logs the round counts and, for each failed call, its tool name and server error key. The line holds no
    /// message text and no argument, so it carries no personal text.
    /// </summary>
    private void LogRoundOutcomes(IEnumerable<ToolCallOutcome> outcomes, int reads, int writes, int deferred)
    {
        var failed = outcomes.Where(outcome => !outcome.ToolResult.Success).ToArray();
        logger.Log(failed.Length > 0 ? LogLevel.Warning : LogLevel.Debug,
            "AI round ran {ReadCount} reads and {WriteCount} writes; failed calls {FailedCount} ({FailedCalls}); deferred reads {DeferredCount}",
            reads, writes, failed.Length, string.Join(", ", failed.Select(outcome => outcome.ToolResult.Name + ":" + FailureKeyOf(outcome))),
            deferred);
    }

    /// <summary>
    /// The server error key of a failed call: the key of a server-composed rejection, else the operation error code,
    /// else the operation status.
    /// </summary>
    private static string FailureKeyOf(ToolCallOutcome outcome) =>
        outcome.FailureKey ?? outcome.OperationResult?.ErrorCode ?? outcome.OperationResult?.Status switch
        {
            AgentOperationStatus.Denied => "policy_denied",
            AgentOperationStatus.UnsupportedByPolicy => "unsupported_by_policy",
            AgentOperationStatus.Failed => "operation_failed",
            _ => "tool_rejected"
        };
}
