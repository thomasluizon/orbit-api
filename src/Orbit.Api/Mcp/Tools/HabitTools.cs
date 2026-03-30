using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class HabitTools(IMediator mediator)
{
    [McpServerTool(Name = "list_habits"), Description("List habits for a date range with schedule info. Returns paginated results with scheduled dates and overdue status.")]
    public async Task<string> ListHabits(
        ClaimsPrincipal user,
        [Description("Start date in YYYY-MM-DD format")] string dateFrom,
        [Description("End date in YYYY-MM-DD format")] string dateTo,
        [Description("Include overdue habits")] bool includeOverdue = true,
        [Description("Search term to filter habits")] string? search = null,
        [Description("Page number (default 1)")] int page = 1,
        [Description("Page size (default 50)")] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetHabitScheduleQuery(
            userId,
            DateOnly.Parse(dateFrom),
            DateOnly.Parse(dateTo),
            includeOverdue,
            search,
            Page: page,
            PageSize: pageSize);

        var result = await mediator.Send(query, cancellationToken);
        if (result.IsFailure)
            return $"Error: {result.Error}";

        var items = result.Value.Items;
        if (items.Count == 0)
            return "No habits found for the given date range.";

        var lines = items.Select(h =>
            $"- [{(h.IsCompleted ? "x" : " ")}] {h.Title} (id: {h.Id})" +
            (h.FrequencyUnit is not null ? $" | {h.FrequencyQuantity}x/{h.FrequencyUnit}" : " | one-time") +
            (h.DueTime is not null ? $" | at {h.DueTime:HH:mm}" : "") +
            (h.IsOverdue ? " | OVERDUE" : "") +
            (h.IsBadHabit ? " | bad habit" : "") +
            (h.ChecklistItems.Count > 0 ? $" | checklist: {h.ChecklistItems.Count(i => i.IsChecked)}/{h.ChecklistItems.Count}" : ""));

        return $"Habits (page {result.Value.Page}/{result.Value.TotalPages}, total: {result.Value.TotalCount}):\n" +
               string.Join("\n", lines);
    }

    [McpServerTool(Name = "get_habit"), Description("Get detailed information about a specific habit by ID.")]
    public async Task<string> GetHabit(
        ClaimsPrincipal user,
        [Description("The habit ID (GUID)")] string habitId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetHabitByIdQuery(userId, Guid.Parse(habitId));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var h = result.Value;
        var info = $"Title: {h.Title}\nID: {h.Id}\n" +
                   $"Status: {(h.IsCompleted ? "Completed" : "Active")}\n" +
                   (h.Description is not null ? $"Description: {h.Description}\n" : "") +
                   (h.FrequencyUnit is not null ? $"Frequency: {h.FrequencyQuantity}x per {h.FrequencyUnit}\n" : "Type: One-time task\n") +
                   $"Due Date: {h.DueDate}\n" +
                   (h.DueTime is not null ? $"Due Time: {h.DueTime:HH:mm}\n" : "") +
                   (h.Days.Count > 0 ? $"Days: {string.Join(", ", h.Days)}\n" : "") +
                   (h.IsBadHabit ? "Bad Habit: Yes\n" : "") +
                   (h.IsGeneral ? "General: Yes\n" : "") +
                   (h.IsFlexible ? "Flexible: Yes\n" : "") +
                   (h.ChecklistItems.Count > 0 ? $"Checklist: {h.ChecklistItems.Count(i => i.IsChecked)}/{h.ChecklistItems.Count} items\n" : "") +
                   $"Created: {h.CreatedAtUtc:yyyy-MM-dd}\n" +
                   (h.Children.Count > 0 ? $"Sub-habits: {string.Join(", ", h.Children.Select(c => c.Title))}\n" : "");
        return info;
    }

    [McpServerTool(Name = "create_habit"), Description("Create a new habit or one-time task.")]
    public async Task<string> CreateHabit(
        ClaimsPrincipal user,
        [Description("Name of the habit")] string title,
        [Description("Due date in YYYY-MM-DD format")] string dueDate,
        [Description("Optional description")] string? description = null,
        [Description("Frequency unit: Day, Week, Month, or Year. Omit for one-time tasks.")] string? frequencyUnit = null,
        [Description("Frequency quantity (default 1). Omit for one-time tasks.")] int? frequencyQuantity = null,
        [Description("Whether this is a bad habit to avoid")] bool isBadHabit = false,
        [Description("Whether this is a general habit with no schedule")] bool isGeneral = false,
        [Description("Whether this is a flexible frequency habit")] bool isFlexible = false,
        [Description("Due time in HH:mm format")] string? dueTime = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        FrequencyUnit? freqUnit = frequencyUnit is not null ? Enum.Parse<FrequencyUnit>(frequencyUnit, true) : null;

        var command = new CreateHabitCommand(
            userId,
            title,
            description,
            freqUnit,
            frequencyQuantity,
            DueDate: DateOnly.Parse(dueDate),
            IsBadHabit: isBadHabit,
            IsGeneral: isGeneral,
            IsFlexible: isFlexible,
            DueTime: dueTime is not null ? TimeOnly.Parse(dueTime) : null);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Created habit '{title}' (id: {result.Value})"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "update_habit"), Description("Update an existing habit's properties. Title is required (pass current title if unchanged). Only change fields you need to modify.")]
    public async Task<string> UpdateHabit(
        ClaimsPrincipal user,
        [Description("The habit ID (GUID)")] string habitId,
        [Description("Habit title (required, pass current title if not changing)")] string title,
        [Description("New description")] string? description = null,
        [Description("New frequency unit: Day, Week, Month, Year")] string? frequencyUnit = null,
        [Description("New frequency quantity")] int? frequencyQuantity = null,
        [Description("New due date in YYYY-MM-DD format")] string? dueDate = null,
        [Description("New due time in HH:mm format")] string? dueTime = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        FrequencyUnit? freqUnit = frequencyUnit is not null ? Enum.Parse<FrequencyUnit>(frequencyUnit, true) : null;

        var command = new UpdateHabitCommand(
            userId,
            Guid.Parse(habitId),
            title,
            description,
            freqUnit,
            frequencyQuantity,
            DueDate: dueDate is not null ? DateOnly.Parse(dueDate) : null,
            DueTime: dueTime is not null ? TimeOnly.Parse(dueTime) : null);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Updated habit {habitId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "delete_habit"), Description("Delete a habit by ID.")]
    public async Task<string> DeleteHabit(
        ClaimsPrincipal user,
        [Description("The habit ID (GUID)")] string habitId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new DeleteHabitCommand(userId, Guid.Parse(habitId));
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Deleted habit {habitId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "log_habit"), Description("Log a habit as completed (or toggle it off if already logged today).")]
    public async Task<string> LogHabit(
        ClaimsPrincipal user,
        [Description("The habit ID (GUID)")] string habitId,
        [Description("Optional note about the completion")] string? note = null,
        [Description("Date to log for in YYYY-MM-DD format (defaults to today)")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new LogHabitCommand(
            userId,
            Guid.Parse(habitId),
            note,
            date is not null ? DateOnly.Parse(date) : null);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Logged habit {habitId} (log id: {result.Value})"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "get_habit_metrics"), Description("Get streak, completion rate, and other metrics for a habit.")]
    public async Task<string> GetHabitMetrics(
        ClaimsPrincipal user,
        [Description("The habit ID (GUID)")] string habitId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetHabitMetricsQuery(userId, Guid.Parse(habitId));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var m = result.Value;
        return $"Metrics for habit {habitId}:\n" +
               $"Current Streak: {m.CurrentStreak} days\n" +
               $"Longest Streak: {m.LongestStreak} days\n" +
               $"Total Completions: {m.TotalCompletions}\n" +
               $"Weekly Completion Rate: {m.WeeklyCompletionRate:P0}\n" +
               $"Monthly Completion Rate: {m.MonthlyCompletionRate:P0}\n" +
               (m.LastCompletedDate is not null ? $"Last Completed: {m.LastCompletedDate}" : "Never completed");
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        return Guid.Parse(claim);
    }
}
