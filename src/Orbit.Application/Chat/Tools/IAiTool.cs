using System.Text.Json;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools;

public interface IAiTool
{
    string Name { get; }
    string Description { get; }
    bool IsReadOnly => false;
    int Order => int.MaxValue;
    object GetParameterSchema();
    Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct);
}

/// <summary>
/// Marks a tool whose <see cref="IAiTool.ExecuteAsync"/> is a single, idempotent load-mutate-save
/// that may be safely re-run from the start when its save loses an optimistic-concurrency race on
/// an xmin-tokened entity (User/Goal/Referral). The dispatcher clears change tracking and retries
/// before surfacing the conflict. Do NOT apply to tools with multiple saves or non-idempotent
/// external side effects.
/// </summary>
public interface IConcurrencyRetryableTool;

public record ToolResult(
    bool Success,
    string? EntityId = null,
    string? EntityName = null,
    string? Error = null,
    object? Payload = null,
    string? ErrorCode = null)
{
    public static ToolResult FromFailure(Result result, string? entityId = null) =>
        new(false, EntityId: entityId, Error: result.Error, ErrorCode: result.ErrorCode);
}
