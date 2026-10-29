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
        "Update an owned habit's checklist. To add a starting step or another item, send only item_to_add; the server appends it unchecked and preserves every stored item, its text, order and checked state. "
        + "Use checklist_items for a full replacement instead. A full list that adds items must preserve the existing list as an unchanged prefix. Never send both fields.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String },
            item_to_add = new { type = JsonSchemaTypes.String, description = "One item to append unchecked. Omit checklist_items; the server keeps all stored items unchanged." },
            checklist_items = new
            {
                type = JsonSchemaTypes.Array,
                description = "Full desired list for replacement. If adding items, preserve all existing items unchanged and in order. Omit when using item_to_add.",
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
        required = new[] { "habit_id" }
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

        var hasAppend = args.TryGetProperty("item_to_add", out var append);
        var hasItems = args.TryGetProperty("checklist_items", out var itemsEl);
        if (hasAppend && hasItems)
            return new ToolResult(false, Error: "Send item_to_add or checklist_items, never both.");
        if (hasAppend && append.ValueKind != JsonValueKind.String)
            return new ToolResult(false, Error: "item_to_add must be a string.");
        if (!hasAppend && (!hasItems || itemsEl.ValueKind != JsonValueKind.Array))
            return new ToolResult(false, Error: "checklist_items is required and must be an array.");

        var habit = await HabitToolHelpers.FindHabitAsync(habitRepository, habitId, userId, ct);
        if (habit is null)
            return HabitToolHelpers.HabitNotFoundResult(habitId);

        if (hasAppend)
        {
            var text = append.GetString()!;
            var validation = habit.CanAppendChecklistItem(text);
            if (validation.IsFailure)
                return ToolResult.FromFailure(validation);
            var appendCommand = new AppendChecklistItemCommand(userId, habitId, text);
            if (checkOnly)
                return await ChatToolArgumentCheck.CheckCommandAsync(mediator, appendCommand, ct);
            var appended = await mediator.Send(appendCommand, ct);
            return appended.IsFailure ? ToolResult.FromFailure(appended)
                : new ToolResult(true, EntityId: habitId.ToString(), EntityName: habit.Title);
        }

        var items = JsonArgumentParser.ParseChecklistItems(args) ?? new List<ChecklistItem>();
        if (items.Count > habit.ChecklistItems.Count
            && !items.Take(habit.ChecklistItems.Count).SequenceEqual(habit.ChecklistItems))
            return new ToolResult(false, Error: "Adding checklist items must preserve every stored item's text, order and checked state. Send only item_to_add to append one item safely.");

        var command = new UpdateChecklistCommand(userId, habitId, items);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityId: habitId.ToString(), EntityName: habit.Title);
    }
}
