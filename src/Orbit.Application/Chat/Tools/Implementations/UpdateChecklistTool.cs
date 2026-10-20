using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateChecklistTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository) : IAiTool, IArgumentCheckTool
{
    public string Name => "update_checklist";

    public string Description =>
        "Replace a habit checklist with the full desired list.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String },
            checklist_items = new
            {
                type = JsonSchemaTypes.Array,
                description = "Full desired list of checklist items. Replaces all existing items.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        text = new { type = JsonSchemaTypes.String },
                        is_checked = new { type = JsonSchemaTypes.Boolean, description = "Default: false." }
                    },
                    required = new[] { "text" }
                }
            }
        },
        required = new[] { "habit_id", "checklist_items" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        if (!args.TryGetProperty("checklist_items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "checklist_items is required and must be an array.");

        var habit = await HabitToolHelpers.FindHabitAsync(habitRepository, habitId, userId, ct);
        if (habit is null)
            return HabitToolHelpers.HabitNotFoundResult(habitId);

        var items = JsonArgumentParser.ParseChecklistItems(args) ?? new List<ChecklistItem>();

        var command = new UpdateChecklistCommand(userId, habitId, items);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityId: habitId.ToString(), EntityName: habit.Title);
    }
}
