using System.Security.Claims;
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

    public static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        if (!Guid.TryParse(claim, out var userId))
            throw new UnauthorizedAccessException("User ID claim is not a valid GUID");
        return userId;
    }

    public static List<Guid> ParseGuidCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Guid.Parse)
            .ToList();

    public static T? DeserializeJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, CaseInsensitiveJsonOptions);

    public static object ToBulkHabitArgs(BulkHabitItemDto dto) => new
    {
        title = dto.Title,
        description = dto.Description,
        frequency_unit = dto.FrequencyUnit,
        frequency_quantity = dto.FrequencyQuantity,
        is_bad_habit = dto.IsBadHabit,
        due_date = dto.DueDate,
        due_time = dto.DueTime,
        is_general = dto.IsGeneral,
        is_flexible = dto.IsFlexible,
        sub_habits = dto.SubHabits?.Select(ToBulkHabitArgs)
    };

    public static string FormatHabitLine(HabitLineData data, int indent)
    {
        var prefix = new string(' ', indent * 2) + "- ";
        var line = $"{prefix}[{(data.IsCompleted ? "x" : " ")}] {data.Title} (id: {data.Id})";
        if (data.FreqUnit is not null) line += $" | {data.FreqQty}x/{data.FreqUnit}";
        else if (!data.IsGeneral) line += " | one-time";
        if (data.IsGeneral) line += " | general";
        if (data.IsFlexible) line += " | flexible";
        if (data.DueTime is not null) line += $" | at {data.DueTime:HH:mm}";
        if (data.IsOverdue) line += " | OVERDUE";
        if (data.IsBadHabit) line += " | bad habit";
        if (data.Checklist.Count > 0) line += $" | checklist: {data.Checklist.Count(i => i.IsChecked)}/{data.Checklist.Count}";
        if (data.Tags.Count > 0) line += $" | tags: {string.Join(", ", data.Tags.Select(t => t.Name))}";
        return line;
    }

    public static void AppendChildren(List<string> lines, IReadOnlyList<HabitScheduleChildItem> children, int indent)
    {
        foreach (var c in children)
        {
            lines.Add(FormatHabitLine(new HabitLineData(c.Id, c.Title, c.FrequencyUnit, c.FrequencyQuantity,
                c.DueTime, c.IsCompleted, false, c.IsBadHabit, c.IsGeneral, c.IsFlexible,
                c.ChecklistItems, c.Tags), indent));
            if (c.Children.Count > 0)
                AppendChildren(lines, c.Children, indent + 1);
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
        List<BulkHabitItemDto>? SubHabits = null);

    public sealed record HabitPositionDto(string HabitId, int Position);

    public sealed record GoalPositionDto(string Id, int Position);

    public sealed record HabitLineData(
        Guid Id, string Title, FrequencyUnit? FreqUnit, int? FreqQty,
        TimeOnly? DueTime, bool IsCompleted, bool IsOverdue, bool IsBadHabit,
        bool IsGeneral, bool IsFlexible,
        IReadOnlyList<ChecklistItem> Checklist, IReadOnlyList<HabitTagItem> Tags);
}
