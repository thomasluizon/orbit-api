using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SkipHabitTool(IMediator mediator, IGenericRepository<Habit> habitRepository) : IAiTool, IArgumentCheckTool, ITransactionalAiTool
{
    public async Task<bool> SupportsAtomicExecutionAsync(Guid userId, JsonElement arguments, CancellationToken ct)
    {
        if (!HabitToolHelpers.TryParseHabitId(arguments, out var id)) return false;
        var habits = await habitRepository.FindAsync(habit => habit.Id == id && habit.UserId == userId,
            query => query.Include(habit => habit.Goals), ct);
        return habits is [var habit] && (habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General
            || !habit.Goals.Any(goal => goal.Type == GoalType.Streak && goal.Status == GoalStatus.Active));
    }

    public string Name => "skip_habit";

    public string Description =>
        "Skip one habit occurrence without logging completion or deleting the habit. Use for skip today or pula só hoje. "
        + "A target lookup must be followed by this write. For several habits use bulk_skip_habits. "
        + "Call this for a requested skip even when a read already shows that occurrence as skipped; this tool validates the requested action against current state. "
        + "Advance recurrence or postpone one-time tasks to tomorrow. Default: today. Returns skip_id for undo.";

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
