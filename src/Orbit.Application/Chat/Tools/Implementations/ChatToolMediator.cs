using MediatR;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

/// <summary>
/// Sends a unit-returning command from an AI tool and shapes the outcome into a <see cref="ToolResult"/>:
/// success carries the entity id, name, and a tool-supplied payload; failure is projected from the result.
/// </summary>
internal static class ChatToolMediator
{
    public static async Task<ToolResult> RunAsync(
        IMediator mediator,
        IRequest<Result> command,
        Guid entityId,
        string entityName,
        object payload,
        CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: entityId.ToString(), EntityName: entityName, Payload: payload)
            : ToolResult.FromFailure(result, entityId.ToString());
    }
}
