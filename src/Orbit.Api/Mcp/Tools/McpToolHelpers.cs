using System.Security.Claims;
using System.Globalization;
using System.Text.Json;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// Pure formatting, parsing, and snake_case argument-mapping helpers shared by the MCP habit and
/// goal toolsets. No injected or instance state — every member is deterministic over its inputs and
/// returns the exact shapes the backing <c>IAiTool</c> schemas and the legacy MCP string contract
/// expect.
/// </summary>
internal static class McpToolHelpers
{
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public static Guid GetUserId(ClaimsPrincipal user) => Orbit.Api.Extensions.HttpContextExtensions.GetUserId(user);

    public static ModelContextProtocol.Protocol.CallToolResult Text(string text) => new()
    {
        Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = text }]
    };

    public static ModelContextProtocol.Protocol.CallToolResult Error(string message, string? code = null) => new()
    {
        IsError = true,
        StructuredContent = code is null ? null : JsonSerializer.SerializeToElement(new { code, message }),
        Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = message }]
    };

    public static List<Guid> ParseGuidCsv(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries)
            .Select(item => McpInputParser.ParseGuid(item, "ids")).ToList();

    public static T DeserializeJson<T>(string json) =>
        ParseJson(json, JsonValueKind.Array, "list").Deserialize<T>(CaseInsensitiveJsonOptions)
            ?? throw new ArgumentException("Invalid list: a JSON array is required.");

    public static JsonElement ParseJson(string json, JsonValueKind kind, string field, bool allowNull = false)
    {
        try
        {
            var value = JsonSerializer.Deserialize<JsonElement>(json);
            if (allowNull && value.ValueKind == JsonValueKind.Null)
                return value;
            if (kind == JsonValueKind.Array && value.ValueKind == kind && value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Null))
                throw new ArgumentException($"Invalid {field}: list items cannot be null.");
            if (value.ValueKind != kind)
                throw new ArgumentException($"Invalid {field}: a JSON {kind.ToString().ToLowerInvariant()} is required.");
            return value;
        }
        catch (JsonException)
        {
            throw new ArgumentException($"Error: {field} must be valid JSON.");
        }
    }

    public static void AddJsonArguments(Dictionary<string, object?> arguments,
        params (string Field, string? Json)[] fields)
    {
        foreach (var (field, json) in fields.Where(value => value.Json is not null))
            arguments[field] = ParseJson(json!, field is "repeat_reminder" ? JsonValueKind.Object : JsonValueKind.Array, field, allowNull: field is "repeat_reminder");
    }

    public static object ToBulkHabitArgs(BulkHabitItemDto dto) => new
    {
        title = dto.Title,
        description = dto.Description,
        emoji = dto.Emoji,
        frequency_unit = dto.FrequencyUnit,
        frequency_quantity = dto.FrequencyQuantity,
        is_bad_habit = dto.IsBadHabit,
        due_date = dto.DueDate,
        due_time = dto.DueTime,
        is_general = dto.IsGeneral,
        is_flexible = dto.IsFlexible,
        sub_habits = dto.SubHabits?.Select(ToBulkHabitArgs),
        repeat_reminder = dto.RepeatReminder
    };

    public static string FormatHabitLine(HabitLineData data, int indent)
    {
        var prefix = new string(' ', indent * 2) + "- ";
        var line = $"{prefix}[{(data.IsCompleted ? "x" : " ")}] {data.Title} (id: {data.Id})";
        line += $" | Emoji: {data.Emoji ?? "None"}";
        if (data.FreqUnit is not null) line += $" | {data.FreqQty}x/{data.FreqUnit}";
        else if (data.Schedule != HabitSchedule.General) line += " | one-time";
        if (data.Schedule == HabitSchedule.General) line += " | general";
        if (data.Schedule == HabitSchedule.Flexible) line += " | flexible";
        if (data.DueTime is not null) line += $" | at {data.DueTime:HH:mm}";
        if (data.IsOverdue) line += " | OVERDUE";
        if (data.IsBadHabit) line += " | bad habit";
        if (data.Checklist.Count > 0) line += $" | checklist: {data.Checklist.Count(i => i.IsChecked)}/{data.Checklist.Count}";
        if (data.Tags.Count > 0) line += $" | tags: {string.Join(", ", data.Tags.Select(t => $"{t.Name} ({t.Id})"))}";
        if (!string.IsNullOrEmpty(data.Details)) line += $" | {data.Details}";
        return line;
    }

    public static void AppendChildren(List<string> lines, IReadOnlyList<HabitScheduleChildItem> children, int indent)
    {
        foreach (var c in children)
        {
            lines.Add(FormatHabitLine(new HabitLineData(c.Id, c.Title, c.FrequencyUnit, c.FrequencyQuantity,
                c.DueTime, c.IsCompleted, false, c.IsBadHabit, c.Schedule ?? HabitSchedule.OneTime,
                c.ChecklistItems, c.Tags, c.Emoji,
                new HabitFieldSummary
                {
                    Description = c.Description,
                    DueTime = c.DueTime,
                    StartDate = c.StartDate,
                    ParentHabitId = c.ParentHabitId,
                    ParentTitle = c.ParentTitle,
                    Tags = c.Tags,
                    DueEndTime = c.DueEndTime,
                    IntervalWeeks = c.IntervalWeeks,
                    Days = c.Days,
                    EndDate = c.EndDate,
                    ReminderEnabled = c.ReminderEnabled == true,
                    ReminderTimes = c.ReminderTimes ?? [],
                    ScheduledReminders = c.ScheduledReminders ?? [],
                    RelativeReminders = c.RelativeReminders ?? [],
                    SlipAlertEnabled = c.SlipAlertEnabled == true,
                    Schedule = c.Schedule ?? HabitSchedule.OneTime,
                    ChecklistItems = c.ChecklistItems
                }.Format(c.DueDate)), indent));
            if (c.Children.Count > 0)
                AppendChildren(lines, c.Children, indent + 1);
        }
    }

    public static void AppendDetailChildren(List<string> lines, IReadOnlyList<HabitChildResponse> children, int indent)
    {
        foreach (var child in children)
        {
            var details = new HabitFieldSummary
            {
                Description = child.Description,
                DueTime = child.DueTime,
                StartDate = child.StartDate,
                ParentHabitId = child.ParentHabitId,
                ParentTitle = child.ParentTitle,
                Tags = child.Tags ?? [],
                DueEndTime = child.DueEndTime,
                IntervalWeeks = child.IntervalWeeks,
                Days = child.Days,
                EndDate = child.EndDate,
                ReminderEnabled = child.ReminderEnabled == true,
                ReminderTimes = child.ReminderTimes ?? [],
                ScheduledReminders = child.ScheduledReminders ?? [],
                RelativeReminders = child.RelativeReminders ?? [],
                SlipAlertEnabled = child.SlipAlertEnabled == true,
                Schedule = child.Schedule ?? HabitSchedule.OneTime,
                ChecklistItems = child.ChecklistItems
            }.Format();
            lines.Add(FormatHabitLine(new HabitLineData(child.Id, child.Title, child.FrequencyUnit,
                child.FrequencyQuantity, child.DueTime, child.IsCompleted, child.IsOverdue, child.IsBadHabit,
                child.Schedule ?? HabitSchedule.OneTime, child.ChecklistItems, child.Tags ?? [], child.Emoji,
                details + string.Create(CultureInfo.InvariantCulture, $" | Due: {child.DueDate:yyyy-MM-dd}")), indent));
            AppendDetailChildren(lines, child.Children, indent + 1);
        }
    }

    public sealed record BulkHabitItemDto(
        string Title,
        string? Description = null,
        string? FrequencyUnit = null,
        int? FrequencyQuantity = null,
        bool IsBadHabit = false,
        string? DueDate = null,
        string? DueTime = null,
        bool IsGeneral = false,
        bool IsFlexible = false,
        List<BulkHabitItemDto>? SubHabits = null,
        string? Emoji = null,
        JsonElement? RepeatReminder = null);

    public sealed record HabitPositionDto(string HabitId, int Position);

    public sealed record GoalPositionDto(string Id, int Position);

    public sealed record HabitLineData(
        Guid Id, string Title, FrequencyUnit? FreqUnit, int? FreqQty,
        TimeOnly? DueTime, bool IsCompleted, bool IsOverdue, bool IsBadHabit,
        HabitSchedule Schedule,
        IReadOnlyList<ChecklistItem> Checklist, IReadOnlyList<HabitTagItem> Tags, string? Emoji, string? Details = null);
}
