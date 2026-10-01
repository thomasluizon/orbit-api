using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class UndoSkipHabitTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "undo_skip_habit";
    public string Description => "Undo one skip using its skip_id. Restores the previous schedule and removes that skip's log. Refuses if the habit changed afterward. Repeating an undo is safe.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit" },
            skip_id = new { type = JsonSchemaTypes.String, description = "Skip ID returned by skip_habit" }
        },
        required = new[] { "habit_id", "skip_id" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();
        if (!args.TryGetProperty("skip_id", out var skipElement)
            || skipElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(skipElement.GetString(), out var skipId) || skipId == Guid.Empty)
            return new ToolResult(false, Error: "Invalid skip_id.");

        var command = new UndoSkipHabitCommand(userId, habitId, skipId);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);
        var result = await mediator.Send(command, ct);
        return result.IsSuccess ? new ToolResult(true, EntityId: habitId.ToString()) : ToolResult.FromFailure(result);
    }
}
