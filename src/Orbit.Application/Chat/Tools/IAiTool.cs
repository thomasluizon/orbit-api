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

public interface IConcurrencyRetryableTool;

/// <summary>
/// A tool that asks the person a question before it writes anything. The chat hold reads
/// the same condition the tool itself reads, so the question reaches the person first and
/// the approval card comes after the answer.
/// </summary>
public interface IClarificationPrecheckTool
{
    bool NeedsClarification(JsonElement args);
}

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
