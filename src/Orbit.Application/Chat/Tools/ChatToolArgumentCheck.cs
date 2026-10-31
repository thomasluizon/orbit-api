using System.Text.Json;
using MediatR;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools;

internal static class ChatToolArgumentCheck
{
    public static async Task<Result> CheckAsync(IAiTool tool, JsonElement args, Func<Task<ToolResult>> prepare)
    {
        var parameterSchema = tool.GetParameterSchema();
        args = OptionalToolArguments.OmitOptionalNulls(args, parameterSchema);
        if (!HasValidShape(tool, args))
            return Result.Failure("Invalid tool arguments.");
        var result = await prepare();
        return result.Success ? Result.Success()
            : result.ErrorCode is { } code ? Result.Failure(result.Error!, code) : Result.Failure(result.Error!);
    }

    internal static bool HasValidShape(IAiTool tool, JsonElement args)
    {
        args = ProactiveProposal.StripMetadata(args);
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        return args.ValueKind == JsonValueKind.Object
            && args.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count()
                == args.EnumerateObject().Count()
            && !args.EnumerateObject().Any(field => field.Name != "preview_item_id"
                && (!AgentArgumentSchema.Accepts(schema, field.Name, field.Value)
                    || !PreservesListEntries(field.Name, field.Value)));
    }

    internal static async Task<PreparedToolIntent?> PrepareAsync(IAiTool tool, JsonElement args,
        Func<Task<ToolResult>> prepare)
    {
        if (!HasValidShape(tool, args))
            return null;
        var result = await prepare();
        return result is { Success: true, Payload: CommandIntent intent }
            ? PreparedToolIntent.Create(intent, args) : null;
    }

    internal sealed record CommandIntent(object Command);

    internal static ToolResult PreparedCommands(IEnumerable<object> commands) =>
        new(true, Payload: new CommandIntent(PreparedToolIntent.Values(commands)));

    private static bool PreservesListEntries(string field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().All(property => PreservesListEntries(property.Name, property.Value));
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(item => item.ValueKind != JsonValueKind.String
                ? PreservesListEntries(field, item) : !string.IsNullOrWhiteSpace(item.GetString()));
        if (field is "text" or "title" or "unit" && value.ValueKind == JsonValueKind.String)
            return !string.IsNullOrWhiteSpace(value.GetString());
        return true;
    }

    public static async Task<ToolResult> CheckCommandAsync(IMediator mediator, object command, CancellationToken ct)
    {
        var result = await mediator.Send(new CheckChatCommandQuery(command), ct);
        return result.IsSuccess ? new ToolResult(true, Payload: new CommandIntent(command)) : ToolResult.FromFailure(result);
    }
}
