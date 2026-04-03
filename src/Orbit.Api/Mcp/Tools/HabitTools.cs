using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class HabitTools(IMediator mediator, IUserDateService userDateService)
{
    private static readonly System.Text.Json.JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string HabitIdDescription = "The habit ID (GUID)";
    private const string DateFromDescription = "Start date in YYYY-MM-DD format";
    private const string DateToDescription = "End date in YYYY-MM-DD format";
    [McpServerTool(Name = "list_habits"), Description("List habits for a date range with schedule info. Returns paginated results with scheduled dates and overdue status.")]
    public async Task<string> ListHabits(
        ClaimsPrincipal user,
        [Description(DateFromDescription)] string dateFrom,
        [Description(DateToDescription)] string dateTo,
        [Description("Include overdue habits")] bool includeOverdue = true,
        [Description("Search term to filter habits")] string? search = null,
        [Description("Page number (default 1)")] int page = 1,
        [Description("Page size (default 50)")] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetHabitScheduleQuery(
            userId,
            DateOnly.Parse(dateFrom, CultureInfo.InvariantCulture),
            DateOnly.Parse(dateTo, CultureInfo.InvariantCulture),
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

        var lines = new List<string>();
        foreach (var h in items)
        {
            lines.Add(FormatHabitLine(h.Id, h.Title, h.FrequencyUnit, h.FrequencyQuantity,
                h.DueTime, h.IsCompleted, h.IsOverdue, h.IsBadHabit, h.IsGeneral, h.IsFlexible,
                h.ChecklistItems, h.Tags, indent: 0));
            AppendChildren(lines, h.Children, indent: 1);
        }

        return $"Habits (page {result.Value.Page}/{result.Value.TotalPages}, total: {result.Value.TotalCount}):\n" +
               string.Join("\n", lines);
    }

    [McpServerTool(Name = "get_habit"), Description("Get detailed information about a specific habit by ID.")]
    public async Task<string> GetHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
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
            IsBadHabit: isBadHabit,
            DueDate: DateOnly.Parse(dueDate, CultureInfo.InvariantCulture),
            IsGeneral: isGeneral,
            Options: new HabitCommandOptions(
                DueTime: dueTime is not null ? TimeOnly.Parse(dueTime, CultureInfo.InvariantCulture) : null,
                IsFlexible: isFlexible));

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Created habit '{title}' (id: {result.Value})"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "update_habit"), Description("Update an existing habit's properties. Title is required (pass current title if unchanged). Only change fields you need to modify.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> UpdateHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
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
            DueDate: dueDate is not null ? DateOnly.Parse(dueDate, CultureInfo.InvariantCulture) : null,
            Options: new UpdateHabitCommandOptions(
                DueTime: dueTime is not null ? TimeOnly.Parse(dueTime, CultureInfo.InvariantCulture) : null));

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Updated habit {habitId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "delete_habit"), Description("Delete a habit by ID.")]
    public async Task<string> DeleteHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
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
        [Description(HabitIdDescription)] string habitId,
        [Description("Optional note about the completion")] string? note = null,
        [Description("Date to log for in YYYY-MM-DD format (defaults to today)")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new LogHabitCommand(
            userId,
            Guid.Parse(habitId),
            note,
            date is not null ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : null);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Logged habit {habitId} (log id: {result.Value})"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "get_habit_metrics"), Description("Get streak, completion rate, and other metrics for a habit.")]
    public async Task<string> GetHabitMetrics(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
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

    [McpServerTool(Name = "skip_habit"), Description("Skip a habit for today (or a specific date). Advances to next scheduled date without logging completion. Only works on recurring habits.")]
    public async Task<string> SkipHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("Date to skip in YYYY-MM-DD format (defaults to today)")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new SkipHabitCommand(
            userId,
            Guid.Parse(habitId),
            date is not null ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : null);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Skipped habit {habitId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "update_checklist"), Description("Update the checklist items for a habit. Pass the full list of items with their checked state.")]
    public async Task<string> UpdateChecklist(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("JSON array of checklist items, each with 'text' (string) and 'isChecked' (boolean)")] string checklistItemsJson,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var items = System.Text.Json.JsonSerializer.Deserialize<List<ChecklistItem>>(
            checklistItemsJson,
            CaseInsensitiveJsonOptions)
            ?? [];

        var command = new UpdateChecklistCommand(userId, Guid.Parse(habitId), items);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Updated checklist for habit {habitId} ({items.Count} items)"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "get_habit_logs"), Description("Get completion logs for a specific habit (last 365 days).")]
    public async Task<string> GetHabitLogs(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetHabitLogsQuery(userId, Guid.Parse(habitId));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var logs = result.Value;
        if (logs.Count == 0)
            return "No logs found for this habit.";

        var lines = logs.Take(50).Select(l =>
            $"- {l.Date:yyyy-MM-dd}" +
            (l.Note is not null ? $" | {l.Note}" : "") +
            $" (id: {l.Id})");

        return $"Logs ({logs.Count} total, showing up to 50):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "get_all_habit_logs"), Description("Get completion logs for all habits within a date range, grouped by habit ID.")]
    public async Task<string> GetAllHabitLogs(
        ClaimsPrincipal user,
        [Description(DateFromDescription)] string dateFrom,
        [Description(DateToDescription)] string dateTo,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetAllHabitLogsQuery(userId, DateOnly.Parse(dateFrom, CultureInfo.InvariantCulture), DateOnly.Parse(dateTo, CultureInfo.InvariantCulture));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var grouped = result.Value;
        if (grouped.Count == 0)
            return "No logs found for the given date range.";

        var lines = grouped.Select(g =>
            $"Habit {g.Key}: {g.Value.Count} logs ({string.Join(", ", g.Value.Take(10).Select(l => l.Date.ToString("yyyy-MM-dd")))})");

        return $"Logs for {grouped.Count} habits:\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "create_sub_habit"), Description("Create a sub-habit under an existing parent habit. Requires Pro subscription.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> CreateSubHabit(
        ClaimsPrincipal user,
        [Description("The parent habit ID (GUID)")] string parentHabitId,
        [Description("Name of the sub-habit")] string title,
        [Description("Optional description")] string? description = null,
        [Description("Frequency unit override: Day, Week, Month, Year")] string? frequencyUnit = null,
        [Description("Frequency quantity override")] int? frequencyQuantity = null,
        [Description("Due time in HH:mm format")] string? dueTime = null,
        [Description("Whether this is a bad habit")] bool isBadHabit = false,
        [Description("Whether this is a flexible frequency habit")] bool isFlexible = false,
        [Description("Due date override in YYYY-MM-DD format")] string? dueDate = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        FrequencyUnit? freqUnit = frequencyUnit is not null ? Enum.Parse<FrequencyUnit>(frequencyUnit, true) : null;

        var command = new CreateSubHabitCommand(
            userId,
            Guid.Parse(parentHabitId),
            title,
            description,
            freqUnit,
            frequencyQuantity,
            IsBadHabit: isBadHabit,
            DueDate: dueDate is not null ? DateOnly.Parse(dueDate, CultureInfo.InvariantCulture) : null,
            Options: new HabitCommandOptions(
                DueTime: dueTime is not null ? TimeOnly.Parse(dueTime, CultureInfo.InvariantCulture) : null,
                IsFlexible: isFlexible));

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Created sub-habit '{title}' (id: {result.Value}) under parent {parentHabitId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "duplicate_habit"), Description("Duplicate a habit (including its sub-habits and tags).")]
    public async Task<string> DuplicateHabit(
        ClaimsPrincipal user,
        [Description("The habit ID to duplicate (GUID)")] string habitId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new DuplicateHabitCommand(userId, Guid.Parse(habitId));
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Duplicated habit {habitId} (new id: {result.Value})"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "bulk_create_habits"), Description("Create multiple habits at once. Each habit can have sub-habits.")]
    public async Task<string> BulkCreateHabits(
        ClaimsPrincipal user,
        [Description("JSON array of habit objects. Each with: title (required), description, frequencyUnit (Day/Week/Month/Year), frequencyQuantity, isBadHabit, dueDate (YYYY-MM-DD), dueTime (HH:mm), isGeneral, isFlexible")] string habitsJson,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var items = System.Text.Json.JsonSerializer.Deserialize<List<BulkHabitItemDto>>(
            habitsJson,
            CaseInsensitiveJsonOptions)
            ?? [];

        var bulkItems = items.Select(MapToBulkHabitItem).ToList();
        var command = new BulkCreateHabitsCommand(userId, bulkItems);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var r = result.Value;
        var successCount = r.Results.Count(x => x.Status == BulkItemStatus.Success);
        var failCount = r.Results.Count(x => x.Status == BulkItemStatus.Failed);
        var lines = r.Results.Select(x =>
            x.Status == BulkItemStatus.Success
                ? $"- OK: {x.Title} (id: {x.HabitId})"
                : $"- FAILED: {x.Title} - {x.Error}");

        return $"Bulk create: {successCount} succeeded, {failCount} failed\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "bulk_delete_habits"), Description("Delete multiple habits at once.")]
    public async Task<string> BulkDeleteHabits(
        ClaimsPrincipal user,
        [Description("Comma-separated habit IDs (GUIDs)")] string habitIds,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var ids = habitIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Guid.Parse).ToList();

        var command = new BulkDeleteHabitsCommand(userId, ids);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var r = result.Value;
        var successCount = r.Results.Count(x => x.Status == BulkItemStatus.Success);
        return $"Bulk delete: {successCount}/{ids.Count} deleted successfully";
    }

    [McpServerTool(Name = "bulk_log_habits"), Description("Log multiple habits as completed at once.")]
    public async Task<string> BulkLogHabits(
        ClaimsPrincipal user,
        [Description("Comma-separated habit IDs (GUIDs)")] string habitIds,
        [Description("Date to log for in YYYY-MM-DD format (defaults to today)")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var ids = habitIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Guid.Parse).ToList();
        var logDate = date is not null ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : (DateOnly?)null;

        var items = ids.Select(id => new BulkLogItem(id, logDate)).ToList();
        var command = new BulkLogHabitsCommand(userId, items);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var r = result.Value;
        var successCount = r.Results.Count(x => x.Status == BulkItemStatus.Success);
        return $"Bulk log: {successCount}/{ids.Count} logged successfully";
    }

    [McpServerTool(Name = "bulk_skip_habits"), Description("Skip multiple habits at once.")]
    public async Task<string> BulkSkipHabits(
        ClaimsPrincipal user,
        [Description("Comma-separated habit IDs (GUIDs)")] string habitIds,
        [Description("Date to skip in YYYY-MM-DD format (defaults to today)")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var ids = habitIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Guid.Parse).ToList();
        var skipDate = date is not null ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : (DateOnly?)null;

        var items = ids.Select(id => new BulkSkipItem(id, skipDate)).ToList();
        var command = new BulkSkipHabitsCommand(userId, items);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var r = result.Value;
        var successCount = r.Results.Count(x => x.Status == BulkItemStatus.Success);
        return $"Bulk skip: {successCount}/{ids.Count} skipped successfully";
    }

    [McpServerTool(Name = "reorder_habits"), Description("Reorder habits by setting new positions.")]
    public async Task<string> ReorderHabits(
        ClaimsPrincipal user,
        [Description("JSON array of objects with 'habitId' (GUID) and 'position' (int)")] string positionsJson,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var items = System.Text.Json.JsonSerializer.Deserialize<List<HabitPositionDto>>(
            positionsJson,
            CaseInsensitiveJsonOptions)
            ?? [];

        var positions = items.Select(p => new HabitPositionUpdate(Guid.Parse(p.HabitId), p.Position)).ToList();
        var command = new ReorderHabitsCommand(userId, positions);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Reordered {positions.Count} habits"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "move_habit_parent"), Description("Move a habit under a different parent, or promote it to top-level by passing null parentId.")]
    public async Task<string> MoveHabitParent(
        ClaimsPrincipal user,
        [Description("The habit ID to move (GUID)")] string habitId,
        [Description("New parent habit ID (GUID), or omit/null to make top-level")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new MoveHabitParentCommand(
            userId,
            Guid.Parse(habitId),
            parentId is not null ? Guid.Parse(parentId) : null);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? parentId is not null
                ? $"Moved habit {habitId} under parent {parentId}"
                : $"Promoted habit {habitId} to top-level"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "link_goals_to_habit"), Description("Link goals to a habit. Pass the full list of goal IDs (replaces existing links).")]
    public async Task<string> LinkGoalsToHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("Comma-separated goal IDs (GUIDs)")] string goalIds,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var ids = goalIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Guid.Parse).ToList();

        var command = new LinkGoalsToHabitCommand(userId, Guid.Parse(habitId), ids);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Linked {ids.Count} goals to habit {habitId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "get_daily_summary"), Description("Get an AI-generated daily summary of habits. Requires Pro subscription and AI summary enabled.")]
    public async Task<string> GetDailySummary(
        ClaimsPrincipal user,
        [Description(DateFromDescription)] string dateFrom,
        [Description(DateToDescription)] string dateTo,
        [Description("Include overdue habits")] bool includeOverdue = true,
        [Description("Language code (en, pt-BR)")] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetDailySummaryQuery(
            userId,
            DateOnly.Parse(dateFrom, CultureInfo.InvariantCulture),
            DateOnly.Parse(dateTo, CultureInfo.InvariantCulture),
            includeOverdue,
            language);

        var result = await mediator.Send(query, cancellationToken);
        if (result.IsFailure)
            return $"Error: {result.Error}";

        var s = result.Value;
        return $"Summary{(s.FromCache ? " (cached)" : "")}:\n{s.Summary}";
    }

    [McpServerTool(Name = "get_retrospective"), Description("Get an AI-generated retrospective analysis of habit performance. Requires Pro subscription.")]
    public async Task<string> GetRetrospective(
        ClaimsPrincipal user,
        [Description("Period: week, month, quarter, semester, or year")] string period = "week",
        [Description("Language code (en, pt-BR)")] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var days = period.ToLowerInvariant() switch
        {
            "week" => 7,
            "month" => 30,
            "quarter" => 90,
            "semester" => 180,
            "year" => 365,
            _ => 7
        };
        var dateFrom = today.AddDays(-days);

        var query = new GetRetrospectiveQuery(userId, dateFrom, today, period, language);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var r = result.Value;
        return $"Retrospective ({period}){(r.FromCache ? " (cached)" : "")}:\n{r.Retrospective}";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        return Guid.Parse(claim);
    }

    // DTOs for JSON deserialization
    private sealed record BulkHabitItemDto(
        string Title,
        string? Description = null,
        string? FrequencyUnit = null,
        int? FrequencyQuantity = null,
        bool IsBadHabit = false,
        string? DueDate = null,
        string? DueTime = null,
        bool IsGeneral = false,
        bool IsFlexible = false,
        List<BulkHabitItemDto>? SubHabits = null);

    private static string FormatHabitLine(Guid id, string title, FrequencyUnit? freqUnit, int? freqQty,
        TimeOnly? dueTime, bool isCompleted, bool isOverdue, bool isBadHabit, bool isGeneral, bool isFlexible,
        IReadOnlyList<ChecklistItem> checklist, IReadOnlyList<HabitTagItem> tags, int indent)
    {
        var prefix = new string(' ', indent * 2) + "- ";
        var line = $"{prefix}[{(isCompleted ? "x" : " ")}] {title} (id: {id})";
        if (freqUnit is not null) line += $" | {freqQty}x/{freqUnit}";
        else if (!isGeneral) line += " | one-time";
        if (isGeneral) line += " | general";
        if (isFlexible) line += " | flexible";
        if (dueTime is not null) line += $" | at {dueTime:HH:mm}";
        if (isOverdue) line += " | OVERDUE";
        if (isBadHabit) line += " | bad habit";
        if (checklist.Count > 0) line += $" | checklist: {checklist.Count(i => i.IsChecked)}/{checklist.Count}";
        if (tags.Count > 0) line += $" | tags: {string.Join(", ", tags.Select(t => t.Name))}";
        return line;
    }

    private static void AppendChildren(List<string> lines, IReadOnlyList<HabitScheduleChildItem> children, int indent)
    {
        foreach (var c in children)
        {
            lines.Add(FormatHabitLine(c.Id, c.Title, c.FrequencyUnit, c.FrequencyQuantity,
                c.DueTime, c.IsCompleted, false, c.IsBadHabit, c.IsGeneral, c.IsFlexible,
                c.ChecklistItems, c.Tags, indent));
            if (c.Children.Count > 0)
                AppendChildren(lines, c.Children, indent + 1);
        }
    }

    private sealed record HabitPositionDto(string HabitId, int Position);

    private static BulkHabitItem MapToBulkHabitItem(BulkHabitItemDto dto)
    {
        FrequencyUnit? freqUnit = dto.FrequencyUnit is not null
            ? Enum.Parse<FrequencyUnit>(dto.FrequencyUnit, true) : null;

        return new BulkHabitItem(
            dto.Title,
            dto.Description,
            freqUnit,
            dto.FrequencyQuantity,
            IsBadHabit: dto.IsBadHabit,
            DueDate: dto.DueDate is not null ? DateOnly.Parse(dto.DueDate, CultureInfo.InvariantCulture) : null,
            DueTime: dto.DueTime is not null ? TimeOnly.Parse(dto.DueTime, CultureInfo.InvariantCulture) : null,
            IsGeneral: dto.IsGeneral,
            IsFlexible: dto.IsFlexible,
            SubHabits: dto.SubHabits?.Select(MapToBulkHabitItem).ToList());
    }
}
