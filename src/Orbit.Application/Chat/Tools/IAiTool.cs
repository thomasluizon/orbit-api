using System.Text.Json;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools;

public interface IAiTool
{
    string Name { get; }
    string Description { get; }
    bool IsReadOnly => false;
    int Order => int.MaxValue;
    /// <summary>
    /// Parameters marked sensitive = true are omitted recursively by field name from evaluation
    /// trace arguments and results. Execution receives the original arguments.
    /// </summary>
    object GetParameterSchema();
    /// <summary>Declares result-only fields, including sensitive = true for credentials returned by a tool.</summary>
    object? GetResultSchema() => null;
    Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct);
}

public interface IChatOnlyAiTool : IAiTool;

public interface IMcpOnlyAiTool;

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
/// A write tool whose database writes share the executor's unit of work for every argument shape, so it joins a
/// change set. Its effects outside the database wait for the commit through AfterCommit. The referral reward job
/// is the exception: it is enqueued before the commit and fails while the referral stays pending. A call to a
/// model provider does not wait for the commit: the tool declares it with <see cref="IProviderCallTool"/>.
/// </summary>
public interface ITransactionalAiTool;

/// <summary>
/// A transactional tool with an argument shape that calls a model provider. The executor opens no transaction
/// for such a call, because an open transaction holds its connection and its row locks for the whole provider
/// call. The tool then commits its own writes. A held call stores arguments that need no provider call, so a
/// change set member never has this shape.
/// </summary>
public interface IProviderCallTool
{
    bool CallsProvider(JsonElement arguments);
}

/// <summary>
/// A transactional tool with one argument shape that writes no row and reports an effect outside the database in
/// its result. That shape never joins a change set, and the executor opens no transaction for it.
/// </summary>
public interface IExternalShapeTool
{
    bool HasExternalShape(JsonElement arguments);
}

public static class AiToolAtomicity
{
    /// <summary>
    /// True when a call of <paramref name="tool"/> with <paramref name="arguments"/> writes to the database only, so
    /// it runs in the executor's unit of work and can join a change set.
    /// </summary>
    public static bool Supports(IAiTool? tool, JsonElement arguments) =>
        tool is ITransactionalAiTool && !(tool is IExternalShapeTool shape && shape.HasExternalShape(arguments));
}

/// <summary>
/// A write tool with an effect outside the database, such as a payment provider, an email or a calendar provider.
/// It never joins a change set.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ExternalEffectToolAttribute : Attribute;

public record ToolResult(
    bool Success,
    string? EntityId = null,
    string? EntityName = null,
    string? Error = null,
    object? Payload = null,
    string? ErrorCode = null)
{
    public static ToolResult FromFailure(Result result, string? entityId = null) =>
        new(false, EntityId: entityId, Error: result.Error, ErrorCode: result.ErrorCode,
            Payload: result.ErrorCode == Common.ErrorCodes.HabitLimitReached
                ? new Models.HabitCapacityFailure(result.Error, result.ErrorArgs) : null);
}
