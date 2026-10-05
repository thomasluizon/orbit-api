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

/// <summary>
/// A write tool that checks its arguments with no write, with the rules its execute path
/// applies that the parameter schema does not declare, such as list caps and ranges. The
/// revise route runs this check on an edited approval preview, so the person cannot approve
/// a value that the tool refuses or reads as another value.
/// </summary>
public interface IArgumentCheckTool
{
    Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct);
}

/// <summary>
/// A tool whose writes share the executor's unit of work and have no external side effects.
/// Confirmation consumption and tool writes commit together; failed outcomes roll both back.
/// </summary>
public interface ITransactionalAiTool;

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
