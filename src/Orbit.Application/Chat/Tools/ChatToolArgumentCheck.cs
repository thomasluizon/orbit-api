using System.Text.Json;
using MediatR;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools;

internal static class ChatToolArgumentCheck
{
    public static async Task<Result> CheckAsync(IAiTool tool, JsonElement args, Func<Task<ToolResult>> prepare)
    {
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        if (args.ValueKind != JsonValueKind.Object
            || args.EnumerateObject().Any(field => field.Name != "preview_item_id"
                && (!AgentArgumentSchema.Accepts(schema, field.Name, field.Value)
                    || !PreservesListEntries(field.Name, field.Value))))
            return Result.Failure("Invalid tool arguments.");
        var result = await prepare();
        return result.Success ? Result.Success() : Result.Failure(result.Error!);
    }

    private static bool PreservesListEntries(string field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().All(property => PreservesListEntries(property.Name, property.Value));
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(item => item.ValueKind != JsonValueKind.String
                ? PreservesListEntries(field, item) : !string.IsNullOrWhiteSpace(item.GetString()));
        if (field == "text" && value.ValueKind == JsonValueKind.String)
            return !string.IsNullOrWhiteSpace(value.GetString());
        return field != "is_read_only" || value.ValueKind != JsonValueKind.Null;
    }

    public static async Task<ToolResult> CheckCommandAsync(IMediator mediator, object command, CancellationToken ct)
    {
        var result = await mediator.Send(new CheckChatCommandQuery(command), ct);
        return result.IsSuccess ? new ToolResult(true) : ToolResult.FromFailure(result);
    }
}
