using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class LogHabitTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "log_habit";

    public string Description =>
        "Log a habit as completed for a specific date (defaults to today). If already logged for that date, this will unlog it (toggle behavior). Use the date parameter to log overdue instances.";

    public object GetParameterSchema() => HabitToolHelpers.SingleHabitDateSchema(
        "ID of the habit to log",
        "ISO date (YYYY-MM-DD) to log for a specific date, e.g. an overdue instance. Defaults to today.");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        var habit = await habitRepository.GetByIdAsync(habitId, ct);
        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        if (habit.UserId != userId)
            return new ToolResult(false, Error: ErrorMessages.HabitNotOwned.Message);

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        DateOnly targetDate = today;
        if (args.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
        {
            if (DateOnly.TryParseExact(dateEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                targetDate = parsed;
            else
                return new ToolResult(false, Error: "Invalid date format. Use YYYY-MM-DD.");
        }

        if (targetDate > today)
            return new ToolResult(false, Error: "Cannot log a future date.");

        var result = await mediator.Send(new LogHabitCommand(userId, habitId, targetDate), ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
