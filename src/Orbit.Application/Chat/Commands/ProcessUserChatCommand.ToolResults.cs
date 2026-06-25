using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    /// <summary>
    /// Builds the frontend-facing ActionResult from a tool execution result.
    /// Returns null for read-only tools (they don't produce action chips).
    /// </summary>
    private static ActionResult? BuildActionResult(AiToolCall call, IAiTool tool, ToolResult result)
    {
        if (tool.IsReadOnly)
            return null;

        if (!result.Success)
        {
            return new ActionResult(
                ToolNameToPascalCase(call.Name),
                ActionStatus.Failed,
                EntityName: result.EntityName,
                Error: result.Error);
        }

        if (call.Name == "suggest_breakdown")
        {
            return new ActionResult(
                ToolNameToPascalCase(call.Name),
                ActionStatus.Suggestion,
                EntityName: result.EntityName,
                SuggestedSubHabits: ExtractSuggestedSubHabits(call.Args));
        }

        return new ActionResult(
            ToolNameToPascalCase(call.Name),
            ActionStatus.Success,
            result.EntityId is not null ? Guid.Parse(result.EntityId) : null,
            result.EntityName);
    }

    private static string BuildOperationSummary(AiToolCall call)
    {
        return $"{ToolNameToPascalCase(call.Name)} requested via chat";
    }

    private static AiToolCallResult BuildToolCallResult(AiToolCall call, AgentOperationResult operationResult)
    {
        return new AiToolCallResult(
            call.Name,
            call.Id,
            operationResult.Status == AgentOperationStatus.Succeeded,
            operationResult.TargetId,
            operationResult.TargetName,
            BuildToolError(operationResult),
            operationResult.Payload);
    }

    private static string? BuildToolError(AgentOperationResult operationResult)
    {
        return operationResult.Status switch
        {
            AgentOperationStatus.Denied => $"Policy denied: {operationResult.PolicyReason}",
            AgentOperationStatus.UnsupportedByPolicy => "Operation is unsupported by policy.",
            AgentOperationStatus.PendingConfirmation => "Confirmation required before this action can run.",
            AgentOperationStatus.Failed => string.Equals(operationResult.PolicyReason, "unexpected_error", StringComparison.Ordinal)
                ? "An unexpected error occurred."
                : operationResult.PolicyReason,
            _ => null
        };
    }

    private static ToolResult ToToolResult(AgentOperationResult operationResult)
    {
        return new ToolResult(
            operationResult.Status == AgentOperationStatus.Succeeded,
            operationResult.TargetId,
            operationResult.TargetName,
            BuildToolError(operationResult),
            operationResult.Payload);
    }

    /// <summary>
    /// Converts snake_case tool names to PascalCase for backward compatibility with the frontend.
    /// e.g., "log_habit" -> "LogHabit", "create_sub_habit" -> "CreateSubHabit"
    /// </summary>
    private static string ToolNameToPascalCase(string toolName)
    {
        var parts = toolName.Split('_');
        return string.Concat(parts.Select(p =>
            string.IsNullOrEmpty(p) ? p : char.ToUpper(p[0], CultureInfo.InvariantCulture) + p[1..]));
    }

    /// <summary>
    /// Returns a copy of the send_support_request args with the correlation id appended to
    /// the message body as a "[trace: {id}]" line, so emailed support tickets carry the trace.
    /// The append respects the support Message length cap; if there is no string message the
    /// args are returned unchanged.
    /// </summary>
    private static JsonElement AppendSupportTrace(JsonElement args, string correlationId)
    {
        var node = JsonNode.Parse(args.GetRawText());
        if (node is not JsonObject argsObject || argsObject["message"] is not JsonValue messageValue
            || !messageValue.TryGetValue(out string? message) || message is null)
        {
            return args;
        }

        var suffix = $"\n\n[trace: {correlationId}]";
        var available = MaxSupportMessageLength - suffix.Length;
        var trimmedMessage = message.Length > available ? message[..Math.Max(0, available)] : message;
        argsObject["message"] = trimmedMessage + suffix;

        return JsonSerializer.Deserialize<JsonElement>(argsObject.ToJsonString());
    }

    /// <summary>
    /// Extracts suggested sub-habits from the suggest_breakdown tool call args
    /// for backward-compatible ActionResult.SuggestedSubHabits.
    /// </summary>
    private static List<AiAction>? ExtractSuggestedSubHabits(JsonElement args)
    {
        if (!args.TryGetProperty("suggested_sub_habits", out var subHabitsEl) ||
            subHabitsEl.ValueKind != JsonValueKind.Array)
            return null;

        var suggestions = new List<AiAction>();
        foreach (var item in subHabitsEl.EnumerateArray())
            suggestions.Add(ParseSingleSubHabit(item));

        return suggestions.Count > 0 ? suggestions : null;
    }

    private static AiAction ParseSingleSubHabit(JsonElement item)
    {
        return new AiAction
        {
            Type = AiActionType.SuggestBreakdown,
            Title = GetStringProperty(item, "title"),
            Description = GetStringProperty(item, "description"),
            FrequencyUnit = GetEnumProperty<FrequencyUnit>(item, "frequency_unit"),
            FrequencyQuantity = GetIntProperty(item, "frequency_quantity"),
            Days = GetDaysProperty(item)
        };
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
    }

    private static TEnum? GetEnumProperty<TEnum>(JsonElement element, string propertyName) where TEnum : struct, Enum
    {
        return element.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            && Enum.TryParse<TEnum>(el.GetString(), true, out var value)
            ? value : null;
    }

    private static int? GetIntProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : null;
    }

    private static List<DayOfWeek>? GetDaysProperty(JsonElement item)
    {
        if (!item.TryGetProperty("days", out var daysEl) || daysEl.ValueKind != JsonValueKind.Array)
            return null;

        var days = new List<DayOfWeek>();
        foreach (var dayEl in daysEl.EnumerateArray())
        {
            if (dayEl.ValueKind == JsonValueKind.String &&
                Enum.TryParse<DayOfWeek>(dayEl.GetString(), true, out var dow))
                days.Add(dow);
        }

        return days;
    }
}
