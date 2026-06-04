using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class BulkDeleteHabitsTool(
    IMediator mediator) : IAiTool
{
    public string Name => "bulk_delete_habits";

    public string Description =>
        "Permanently delete multiple habits in a single operation. Only delete habits the user explicitly asked to remove.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                description = "IDs of the habits to delete.",
                items = new { type = JsonSchemaTypes.String }
            }
        },
        required = new[] { "habit_ids" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "habit_ids is required and must be an array.");

        var habitIds = JsonArgumentParser.ParseGuidArray(args, "habit_ids") ?? new List<Guid>();
        if (habitIds.Count == 0)
            return new ToolResult(false, Error: "No valid habit IDs provided.");

        var result = await mediator.Send(new BulkDeleteHabitsCommand(userId, habitIds), ct);

        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        var successCount = result.Value.Results.Count(item => item.Status == BulkItemStatus.Success);
        return new ToolResult(true, EntityName: $"{successCount}/{habitIds.Count} habits deleted", Payload: result.Value);
    }
}
