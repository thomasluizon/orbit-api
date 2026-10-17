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
        "Skip a habit for a specific date (defaults to today). For recurring habits, advances the due date to the next scheduled occurrence. For one-time tasks, postpones to tomorrow. Does not log completion. Works on habits that are due or overdue. Returns skip_id for undo_skip_habit.";

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
