using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class UnlogHabitTool(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IPayGateService payGate,
    IUserStreakService userStreakService,
    IGoalCompletionService goalCompletionService) : IAiTool, IArgumentCheckTool, ITransactionalAiTool, IConcurrencyRetryableTool
{
    private static readonly UnlogHabitArgumentsValidator Validator = new();

    public string Name => "unlog_habit";
    public string Description => "Undo a habit completion for a specific date (defaults to today). Removes one completion without creating a log. Fails if no completion exists for that date. Use log_habit to mark it again.";

    public object GetParameterSchema() => HabitToolHelpers.SingleHabitDateSchema(
        "ID of the habit whose completion to undo",
        "ISO date (YYYY-MM-DD) of the completion to undo. Defaults to today.");

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, checkOnly: false, ct);

    public Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, checkOnly: true, ct));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, bool checkOnly, CancellationToken ct)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("habit_id", out var idElement) || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var targetDate = today;
        if (args.TryGetProperty("date", out var dateElement)
            && (dateElement.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out targetDate)))
            return new ToolResult(false, Error: "Invalid date format. Use YYYY-MM-DD.");

        var validation = await Validator.ValidateAsync(new UnlogHabitArguments(userId, habitId), ct);
        if (!validation.IsValid)
            return new ToolResult(false, Error: validation.ToString());

        if (checkOnly)
        {
            var prepared = await LoadAsync(habitId, userId, targetDate, ct);
            return prepared.IsSuccess ? new ToolResult(true) : ToolResult.FromFailure(prepared);
        }

        var result = await HabitCeilingLock.ExecuteEntryAsync<Habit, ToolResult>(
            unitOfWork, userId, payGate,
            token => LoadAsync(habitId, userId, targetDate, token),
            HabitLiveRootEntry.FromUnlog,
            async (habit, token) =>
            {
                var unlogged = habit.Unlog(targetDate);
                if (unlogged.IsFailure)
                    return unlogged.PropagateError<ToolResult>();

                await unitOfWork.SaveChangesAsync(token);
                await goalCompletionService.SyncDerivedGoalsAsync(userId, habit.Goals.Select(goal => goal.Id).ToList(),
                    today, cancellationToken: token);
                await userStreakService.RecalculateAsync(userId, awardFreezeIfEligible: false, cancellationToken: token);
                await unitOfWork.SaveChangesAsync(token);
                return Result.Success(new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title));
            }, ct);
        return result.IsSuccess ? result.Value : ToolResult.FromFailure(result);
    }

    private async Task<Result<Habit>> LoadAsync(Guid habitId, Guid userId, DateOnly date, CancellationToken ct)
    {
        var habit = await habitRepository.FindOneTrackedAsync(habit => habit.Id == habitId,
            query => query.Include(habit => habit.Logs.Where(log => log.Date == date))
                .Include(habit => habit.Goals)
                .AsSplitQuery(), ct);
        if (habit is null)
            return Result.Failure<Habit>($"Habit {habitId} not found.");
        if (habit.UserId != userId)
            return Result.Failure<Habit>(ErrorMessages.HabitNotOwned);
        if (!habit.Logs.Any(log => log.Date == date && log.Value > 0 && !log.IsDeleted))
            return Result.Failure<Habit>(DomainErrors.LogNotFoundForDate);
        return Result.Success(habit);
    }
}
