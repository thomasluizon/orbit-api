using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static partial class ProactiveProposal
{
    private static string? FieldInstruction(string field) => field switch
    {
        "reminder_enabled" => "For a new dated OneTime task with due_time and no reminder preference, set true and include reminder_enabled and reminder_times in suggested_fields. Preserve explicit false and all requested or stored reminders.",
        "reminder_times" => "Minutes before due_time; 0 means at that time. For an unrequested timed-task reminder, set [0], set reminder_enabled=true and list both names in suggested_fields. Prescribed fixed clock times use scheduled_reminders instead.",
        "parent_habit_id" => "When unrequested, choose a fitting owned routine from offer_account_facts.Habits, set its ID here and list parent_habit_id in suggested_fields. Do not leave it null when a clear fit exists. Ambiguous parents require a preview choice. Keep the bulk container parent unset for item-level suggestions.",
        "goal_ids" => "Use matching owned IDs from offer_account_facts.ActiveGoals. When unrequested, set the matching IDs here and list goal_ids in suggested_fields. Preserve stored links; query only missing facts, never create an equivalent goal.",
        _ => null
    };

    internal static AiToolCall CompleteCreateReminder(AiToolCall call, AgentClientContext? client)
    {
        if (!Enabled(client) || client!.DismissedOfferKeys is { Count: > 0 }
            || call.Name is not ("create_habit" or "bulk_create_habits") || call.Args.ValueKind != JsonValueKind.Object)
            return call;
        var root = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        if (call.Name == "bulk_create_habits" && root["habits"] is JsonArray habits)
        {
            foreach (var item in habits.OfType<JsonObject>()) CompleteReminderNode(item);
        }
        else CompleteReminderNode(root);
        return call with { Args = JsonSerializer.SerializeToElement(root) };
    }

    private static void CompleteReminderNode(JsonObject node)
    {
        if (node["sub_habits"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>()) CompleteReminderNode(child);
        if (node["habit_id"] is not null || node["frequency_unit"]?.ToString() != "OneTime"
            || !DateOnly.TryParseExact(node["due_date"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !TimeOnly.TryParseExact(node["due_time"]?.ToString(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || ReminderFields.Any(field => HasValue(node[field]))
            || node[Argument] is not (null or JsonArray or JsonObject)) return;
        if (node[Argument] is JsonObject values)
        {
            if (ReminderFields.Any(field => HasValue(values[field]))) return;
            values["reminder_enabled"] = true;
            values["reminder_times"] = new JsonArray(0);
            return;
        }
        if (node[Argument] is not JsonArray names)
        {
            names = new JsonArray();
            node[Argument] = names;
        }
        node["reminder_enabled"] = true;
        node["reminder_times"] = new JsonArray(0);
        foreach (var field in new[] { "reminder_enabled", "reminder_times" })
            if (!names.Any(name => name?.ToString() == field)) names.Add(field);
    }
}
