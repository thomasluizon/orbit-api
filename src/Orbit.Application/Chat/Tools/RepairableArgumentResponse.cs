using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools;

/// <summary>In-process evidence that argument validation failed before dispatch or approval.</summary>
public sealed record RepairableArgumentResponse : AgentExecuteOperationResponse
{
    public RepairableArgumentResponse(AgentExecuteOperationResponse response)
        : base(response.Operation, response.PendingOperation, response.PolicyDenial) { }
}
