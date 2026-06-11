using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateChecklistTool(
    IMediator mediator) : IAiTool
{
    public string Name => "update_checklist";

    public string Description =>
        "Replace the checklist items on a habit. Pass the full desired list - existing items are overwritten.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit whose checklist to replace" },
            checklist_items = new
            {
                type = JsonSchemaTypes.Array,
                description = "Full desired list of checklist items. Replaces all existing items.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        text = new { type = JsonSchemaTypes.String, description = "Checklist item text" },
                        is_checked = new { type = JsonSchemaTypes.Boolean, description = "Whether checked. Defaults to false." }
                    },
                    required = new[] { "text" }
                }
            }
        },
        required = new[] { "habit_id", "checklist_items" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        if (!args.TryGetProperty("checklist_items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "checklist_items is required and must be an array.");

        var items = JsonArgumentParser.ParseChecklistItems(args) ?? new List<ChecklistItem>();

        var result = await mediator.Send(new UpdateChecklistCommand(userId, habitId, items), ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityId: habitId.ToString());
    }
}
