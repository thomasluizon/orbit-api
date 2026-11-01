using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class LogHabitTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IAiTool, IArgumentCheckTool
{
    public string Name => "log_habit";

    public string Description =>
        "Log habit completion for a date, defaulting to today. Already logged dates fail; use unlog_habit to undo. Overdue dates work.";

    public object GetParameterSchema() => HabitToolHelpers.SingleHabitDateSchema(
        "ID of the habit to log",
        "ISO date (YYYY-MM-DD) to log for a specific date, e.g. an overdue instance. Defaults to today.");

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

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
            return new ToolResult(false, Error: ErrorMessages.CannotLogFutureDate.Message);

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId,
            query => query.Include(h => h.Logs.Where(log => log.Date == targetDate)), ct);
        if (habit is null)
            return HabitToolHelpers.HabitNotFoundResult(habitId);
        if (habit.UserId != userId)
            return new ToolResult(false, Error: ErrorMessages.HabitNotOwned.Message);
        if (habit.Schedule != HabitSchedule.Flexible && !habit.IsBadHabit
            && (habit.Schedule != HabitSchedule.General || habit.IsCompleted)
            && habit.Logs.Any(log => log.Date == targetDate && !log.IsDeleted))
            return ToolResult.FromFailure(Result.Failure(DomainErrors.AlreadyLoggedForDate));

        var command = new LogHabitCommand(userId, habitId, targetDate, ToggleExisting: false);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
