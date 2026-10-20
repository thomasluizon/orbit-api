using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SkipHabitTool(IMediator mediator, IGenericRepository<Habit> habitRepository) : IAiTool, IArgumentCheckTool
{
    public string Name => "skip_habit";

    public string Description =>
        "Skip a due/overdue habit without completion: advance recurrence or postpone one-time tasks to tomorrow. Default: today. Returns skip_id for undo.";

    public object GetParameterSchema() => HabitToolHelpers.SingleHabitDateSchema(
        "ID of the habit to skip",
        "ISO date (YYYY-MM-DD) to skip a specific instance. Defaults to today.");

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        DateOnly? date = null;
        if (args.TryGetProperty("date", out var dateElement) && dateElement.ValueKind == JsonValueKind.String)
        {
            if (!DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return new ToolResult(false, Error: "Invalid date format. Use YYYY-MM-DD.");
            date = parsed;
        }

        var command = new SkipHabitCommand(userId, habitId, date);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);
        var habit = await HabitToolHelpers.FindHabitAsync(habitRepository, habitId, userId, ct);
        return new ToolResult(true, EntityId: habitId.ToString(), EntityName: habit?.Title,
            Payload: new { skip_id = result.Value.SkipId });
    }
}
