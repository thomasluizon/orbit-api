using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SkipHabitTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "skip_habit";

    public string Description =>
        "Skip a habit for a specific date (defaults to today). For recurring habits, advances the due date to the next scheduled occurrence. For one-time tasks, postpones to tomorrow. Does not log completion. Works on habits that are DUE or OVERDUE. Use when the user says 'skip', 'pass on', 'not today', 'postpone', or 'dismiss' for a habit.";

    public object GetParameterSchema() => HabitToolHelpers.SingleHabitDateSchema(
        "ID of the habit to skip",
        "ISO date (YYYY-MM-DD) to skip a specific instance. Defaults to today.");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId && h.UserId == userId,
            q => q.Include(h => h.Logs),
            ct);

        if (habit is null)
            return HabitToolHelpers.HabitNotFoundResult(habitId);

        if (habit.IsCompleted)
            return new ToolResult(false, Error: "Cannot skip a completed habit.");

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        if (habit.FrequencyUnit is null)
        {
            habit.PostponeTo(today.AddDays(1));
            return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
        }

        var targetDate = ParseTargetDate(args, today);
        if (targetDate is null)
            return new ToolResult(false, Error: "Invalid date format. Use YYYY-MM-DD.");

        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, ct);
        return await SkipRecurringHabit(habit, targetDate.Value, today, weekStartDay, ct);
    }

    private static DateOnly? ParseTargetDate(JsonElement args, DateOnly today)
    {
        if (!args.TryGetProperty("date", out var dateEl) || dateEl.ValueKind != JsonValueKind.String)
            return today;

        return DateOnly.TryParseExact(dateEl.GetString(), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private async Task<ToolResult> SkipRecurringHabit(Habit habit, DateOnly targetDate, DateOnly today, int weekStartDay, CancellationToken ct)
    {
        if (targetDate > today)
            return new ToolResult(false, Error: "Cannot skip a future date.");

        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return new ToolResult(false, Error: "Cannot skip a habit that is not yet due.");

        if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
            return new ToolResult(false, Error: "Habit is not scheduled on this date.");

        if (habit.IsFlexible)
        {
            var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs, weekStartDay);
            if (remaining <= 0)
                return new ToolResult(false, Error: "All instances for this period have already been completed or skipped.");

            var skipResult = habit.SkipFlexible(targetDate);
            if (skipResult.IsFailure)
                return ToolResult.FromFailure(skipResult);

            await habitLogRepository.AddAsync(skipResult.Value, ct);
        }
        else
        {
            habit.AdvanceDueDate(targetDate);
        }

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
